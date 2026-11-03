namespace GarageWatch.Application.Portal;

/// <summary>
/// Integration credentials owned by the portal (P32). Values are stored encrypted, delivered only to
/// server-side consumers and never serialized to a browser, microfrontend or log.
/// </summary>
public static class IntegrationCredentialIds
{
    public const string ActivityWatch = "activitywatch";

    private static readonly HashSet<string> SupportedIds = new(StringComparer.Ordinal) { ActivityWatch };

    public static bool IsSupported(string? integrationId) =>
        integrationId is not null && SupportedIds.Contains(integrationId);

    public static string Normalize(string integrationId) => integrationId.Trim().ToLowerInvariant();
}

/// <summary>Encrypted credential material with the key version used to seal it.</summary>
public sealed record SealedIntegrationCredential(byte[] Ciphertext, byte[] Nonce, byte[] Tag, string KeyVersion);

/// <summary>Authenticated encryption boundary for integration credentials.</summary>
public interface IIntegrationCredentialProtector
{
    bool IsConfigured { get; }

    string? ActiveKeyVersion { get; }

    SealedIntegrationCredential Seal(string plaintext, string associatedData);

    /// <summary>Returns the plaintext only when a configured key version authenticates the payload.</summary>
    string? TryOpen(byte[] ciphertext, byte[] nonce, byte[] tag, string keyVersion, string associatedData);

    bool SupportsKeyVersion(string keyVersion);
}

/// <summary>Non-secret projection of a stored credential; never carries the secret value.</summary>
public sealed record IntegrationCredentialStatus(string IntegrationId, bool Configured, string? KeyVersion,
    DateTimeOffset? UpdatedAtUtc, long Version);

public sealed record ResolvedIntegrationCredential(string Value, string KeyVersion);

public interface IIntegrationCredentialStore
{
    /// <summary>Resolves the plaintext secret for a server-side consumer, rotating the key version on access.</summary>
    Task<ResolvedIntegrationCredential?> ResolveAsync(string integrationId,
        CancellationToken cancellationToken = default);

    Task<IntegrationCredentialStatus> GetStatusAsync(string integrationId,
        CancellationToken cancellationToken = default);

    Task<IntegrationCredentialStatus> SetAsync(Guid actorUserId, string integrationId, string secret,
        CancellationToken cancellationToken = default);

    Task<IntegrationCredentialStatus> RemoveAsync(Guid actorUserId, string integrationId,
        CancellationToken cancellationToken = default);
}

/// <summary>A credential row exists but no configured key can authenticate it.</summary>
public sealed class IntegrationCredentialUnreadableException(string integrationId)
    : Exception($"integration_credential_unreadable:{integrationId}");

public sealed class IntegrationCredentialInputException(string message) : Exception(message);

/// <summary>Encryption is not configured, so writes must fail instead of storing plaintext.</summary>
public sealed class IntegrationCredentialEncryptionUnavailableException()
    : Exception("integration_credential_encryption_unavailable");
