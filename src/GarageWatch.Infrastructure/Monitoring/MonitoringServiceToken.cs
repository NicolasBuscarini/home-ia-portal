using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Monitoring;

/// <summary>
/// Emissor do token serviço-a-serviço do portal para o módulo de Monitoramento (P28).
/// O segredo é compartilhado apenas entre os dois serviços e nunca chega ao frontend.
/// </summary>
public sealed record MonitoringServiceTokenOptions(string? Secret, string Issuer, string Audience,
    string ResidenceId, int LifetimeSeconds = 60)
{
    public const string SecretKey = "MONITORING_SERVICE_TOKEN_SECRET";
    public const string IssuerKey = "MONITORING_SERVICE_TOKEN_ISSUER";
    public const string AudienceKey = "MONITORING_SERVICE_TOKEN_AUDIENCE";
    public const string ResidenceKey = "PORTAL_RESIDENCE_ID";

    public bool Configured =>
        !string.IsNullOrWhiteSpace(Secret) && Encoding.UTF8.GetByteCount(Secret) >= 32;

    public static MonitoringServiceTokenOptions FromConfiguration(IConfiguration configuration) => new(
        configuration[SecretKey]?.Trim(),
        (configuration[IssuerKey] ?? "home-ia-portal").Trim(),
        (configuration[AudienceKey] ?? "monitoring").Trim(),
        (configuration[ResidenceKey] ?? "home").Trim());
}

public sealed class MonitoringServiceTokenIssuer(MonitoringServiceTokenOptions options, TimeProvider timeProvider)
{
    public const string ReadScope = "monitoring.read";
    public const string WriteScope = "monitoring.write";
    private const string Subject = "portal";

    public string? Issue(string scope)
    {
        if (!options.Configured)
            return null;

        var now = timeProvider.GetUtcNow();
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" })));
        var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iss = options.Issuer,
            aud = options.Audience,
            sub = Subject,
            residence_id = options.ResidenceId,
            scope,
            iat = now.ToUnixTimeSeconds(),
            exp = now.AddSeconds(options.LifetimeSeconds).ToUnixTimeSeconds(),
            jti = Guid.NewGuid().ToString("N")
        })));
        var signature = Base64Url.EncodeToString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(options.Secret!),
            Encoding.ASCII.GetBytes($"{header}.{payload}")));
        return $"{header}.{payload}.{signature}";
    }
}

/// <summary>
/// Anexa a credencial de curta duração em toda chamada ao módulo. Leitura recebe apenas
/// <c>monitoring.read</c>; comandos recebem <c>monitoring.write</c>.
/// </summary>
public sealed class MonitoringServiceTokenHandler(MonitoringServiceTokenIssuer issuer) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var scope = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head
            ? MonitoringServiceTokenIssuer.ReadScope
            : MonitoringServiceTokenIssuer.WriteScope;
        var token = issuer.Issue(scope);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return base.SendAsync(request, cancellationToken);
    }
}
