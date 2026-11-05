using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace GarageWatch.Application.PlatformEvents;

/// <summary>
/// Canonical, project-owned JSON form used by the stable payload hash. Only the event portion is
/// hashed; <c>publicId</c>, <c>recordedAtUtc</c> and <c>payloadHash</c> are server-owned and
/// therefore excluded. Metadata keys are sorted ordinally so insertion order never changes the hash.
/// </summary>
public static class PlatformEventCanonicalizer
{
    public static byte[] Canonicalize(PlatformEventV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", value.SchemaVersion);
            writer.WriteString("eventId", value.EventId.ToString("D"));
            writer.WriteString("eventType", value.EventType);
            writer.WriteString("category", value.Category);
            writer.WriteString("severity", value.Severity);
            writer.WriteString("outcome", value.Outcome);
            writer.WriteString("occurredAtUtc",
                value.OccurredAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("source", value.Source);
            WriteNullableString(writer, "sourceVersion", value.SourceVersion);
            writer.WriteString("emitter", value.Emitter);
            WriteActor(writer, value.Actor);
            WriteReference(writer, "subject", value.Subject);
            WriteReference(writer, "resource", value.Resource);
            WriteNullableString(writer, "correlationId", value.CorrelationId);
            WriteNullableString(writer, "causationId", value.CausationId);
            WriteNullableString(writer, "requestId", value.RequestId);
            WriteNullableString(writer, "sessionId", value.SessionId);
            WriteNullableString(writer, "traceId", value.TraceId);
            WriteNullableString(writer, "spanId", value.SpanId);
            WriteHttp(writer, value.Http);
            WriteClient(writer, value.Client);
            writer.WritePropertyName("metadata");
            if (value.Metadata is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                foreach (var pair in value.Metadata.OrderBy(item => item.Key, StringComparer.Ordinal))
                    WriteMetadataValue(writer, pair.Key, pair.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    public static string ComputePayloadHash(PlatformEventV1 value) =>
        Convert.ToHexStringLower(SHA256.HashData(Canonicalize(value)));

    public static PlatformEventRecordV1 CreateRecord(
        PlatformEventV1 value, string publicId, DateTimeOffset recordedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!PlatformEventPublicId.IsValid(publicId))
            throw new ArgumentException("ID público de evento inválido.", nameof(publicId));

        return new PlatformEventRecordV1(
            publicId, recordedAtUtc.ToUniversalTime(), ComputePayloadHash(value), value);
    }

    public static byte[] SerializeMetadata(IReadOnlyDictionary<string, object?>? metadata)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (metadata is not null)
            {
                foreach (var pair in metadata.OrderBy(item => item.Key, StringComparer.Ordinal))
                    WriteMetadataValue(writer, pair.Key, pair.Value);
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Restores the typed values accepted by <see cref="WriteMetadataValue"/> so a stored event can
    /// be hashed again for the administrative integrity check.
    /// </summary>
    public static IReadOnlyDictionary<string, object?>? DeserializeMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Metadata do evento não é um objeto.");
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            metadata[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Number when property.Value.TryGetInt64(out var integer) => integer,
                JsonValueKind.Number when property.Value.TryGetDecimal(out var number) => number,
                JsonValueKind.Number => property.Value.GetDouble(),
                _ => throw new JsonException("Valor de metadata do evento não suportado.")
            };
        }

        return metadata;
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static void WriteActor(Utf8JsonWriter writer, PlatformEventActorV1? actor)
    {
        writer.WritePropertyName("actor");
        if (actor is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        WriteNullableString(writer, "userPublicId", actor.UserPublicId);
        WriteNullableString(writer, "username", actor.Username);
        WriteNullableString(writer, "displayName", actor.DisplayName);
        writer.WriteEndObject();
    }

    private static void WriteReference(Utf8JsonWriter writer, string name, PlatformEventReferenceV1? reference)
    {
        writer.WritePropertyName(name);
        if (reference is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("type", reference.Type);
        writer.WriteString("publicId", reference.PublicId);
        writer.WriteEndObject();
    }

    private static void WriteHttp(Utf8JsonWriter writer, PlatformEventHttpV1? http)
    {
        writer.WritePropertyName("http");
        if (http is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("method", http.Method);
        writer.WriteString("route", http.Route);
        writer.WriteNumber("statusCode", http.StatusCode);
        writer.WriteNumber("durationMs", http.DurationMs);
        writer.WriteEndObject();
    }

    private static void WriteClient(Utf8JsonWriter writer, PlatformEventClientV1? client)
    {
        writer.WritePropertyName("client");
        if (client is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        WriteNullableString(writer, "userAgent", client.UserAgent);
        WriteNullableString(writer, "browser", client.Browser);
        WriteNullableString(writer, "os", client.Os);
        WriteNullableString(writer, "device", client.Device);
        WriteNullableString(writer, "ip", client.Ip);
        writer.WriteEndObject();
    }

    private static void WriteMetadataValue(Utf8JsonWriter writer, string key, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull(key);
                break;
            case string text:
                writer.WriteString(key, text);
                break;
            case bool flag:
                writer.WriteBoolean(key, flag);
                break;
            case int number:
                writer.WriteNumber(key, number);
                break;
            case long number:
                writer.WriteNumber(key, number);
                break;
            case double number:
                writer.WriteNumber(key, number);
                break;
            case decimal number:
                writer.WriteNumber(key, number);
                break;
            default:
                throw new ArgumentException("Valor de metadado não suportado.", nameof(value));
        }
    }
}
