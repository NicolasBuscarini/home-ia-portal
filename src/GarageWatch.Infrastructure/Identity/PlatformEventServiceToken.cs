using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GarageWatch.Application.PlatformEvents;
using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Identity;

/// <summary>
/// Inbound service credential for <c>POST /api/v1/events</c>. It is deliberately validated without
/// a new authentication scheme/package: HS256, portal audience, residence and the narrow
/// <c>events.write</c> scope. The service identity must equal the event source.
/// </summary>
public sealed record PlatformEventServiceTokenOptions(string? Secret, string Audience, string ResidenceId)
{
    public const string SecretKey = "MONITORING_SERVICE_TOKEN_SECRET";
    public const string AudienceKey = "EVENT_SERVICE_TOKEN_AUDIENCE";
    public const string ResidenceKey = "PORTAL_RESIDENCE_ID";
    public const string RequiredScope = "events.write";

    public bool Configured =>
        !string.IsNullOrWhiteSpace(Secret) && Encoding.UTF8.GetByteCount(Secret) >= 32;

    public static PlatformEventServiceTokenOptions FromConfiguration(IConfiguration configuration) => new(
        configuration[SecretKey]?.Trim(),
        (configuration[AudienceKey] ?? "home-ia-portal").Trim(),
        (configuration[ResidenceKey] ?? "home").Trim());
}

public sealed class PlatformEventServiceTokenValidator(
    PlatformEventServiceTokenOptions options, TimeProvider timeProvider)
{
    public bool TryValidate(string? token, out string subject) =>
        TryValidate(token, PlatformEventServiceTokenOptions.RequiredScope, out subject);

    /// <summary>Validates the signature, audience, residence and one narrow scope supplied by the endpoint.</summary>
    public bool TryValidate(string? token, string requiredScope, out string subject)
    {
        subject = string.Empty;
        if (!options.Configured || string.IsNullOrWhiteSpace(token) || token.Length > 4096)
            return false;

        var parts = token.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty)) return false;

        byte[] headerBytes;
        byte[] payloadBytes;
        byte[] signature;
        try
        {
            headerBytes = Base64Url.DecodeFromChars(parts[0]);
            payloadBytes = Base64Url.DecodeFromChars(parts[1]);
            signature = Base64Url.DecodeFromChars(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Secret!),
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
        if (!CryptographicOperations.FixedTimeEquals(expected, signature)) return false;

        try
        {
            using var header = JsonDocument.Parse(headerBytes);
            if (!string.Equals(ReadString(header.RootElement, "alg"), "HS256", StringComparison.Ordinal) ||
                !string.Equals(ReadString(header.RootElement, "typ"), "JWT", StringComparison.Ordinal))
                return false;

            using var payload = JsonDocument.Parse(payloadBytes);
            var root = payload.RootElement;
            var service = ReadString(root, "sub");
            if (service is null || !PlatformEventSources.IsValid(service)) return false;
            if (!string.Equals(ReadString(root, "aud"), options.Audience, StringComparison.Ordinal)) return false;
            if (!string.Equals(ReadString(root, "residence_id"), options.ResidenceId, StringComparison.Ordinal))
                return false;
            if (!HasScope(ReadString(root, "scope"), requiredScope)) return false;
            if (!root.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var expiresAt) ||
                expiresAt <= timeProvider.GetUtcNow().ToUnixTimeSeconds())
                return false;
            if (root.TryGetProperty("iat", out var issuedAt) &&
                (!issuedAt.TryGetInt64(out var issuedAtSeconds) ||
                 issuedAtSeconds > timeProvider.GetUtcNow().ToUnixTimeSeconds() + 60))
                return false;

            subject = service;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasScope(string? scope, string requiredScope) =>
        scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(requiredScope, StringComparer.Ordinal) == true;

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
