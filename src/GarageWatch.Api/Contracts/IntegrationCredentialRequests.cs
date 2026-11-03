namespace GarageWatch.Api.Contracts;

/// <summary>Write-only payload for an integration credential; the value is never echoed back.</summary>
public sealed record SetIntegrationCredentialRequest(string Secret);

/// <summary>Non-secret credential projection safe for administrators and logs.</summary>
public sealed record IntegrationCredentialStatusResponse(string IntegrationId, bool Configured, string Source,
    string? KeyVersion, DateTimeOffset? UpdatedAtUtc, long Version);
