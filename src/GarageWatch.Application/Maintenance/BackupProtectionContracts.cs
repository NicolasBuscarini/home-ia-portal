namespace GarageWatch.Application.Maintenance;

/// <summary>
/// Self-contained AES-256-GCM envelope. The nonce and tag travel together with the ciphertext so an
/// encrypted backup artifact is a single opaque blob that can be decrypted knowing only its key
/// version; the manifest records the key version and the ciphertext hash.
/// </summary>
public sealed record EncryptedBackupEnvelope(byte[] Nonce, byte[] Ciphertext, byte[] Tag);

/// <summary>
/// Protects backup artifacts at rest. A missing or invalid key leaves the protector unconfigured and
/// the backup service fails closed instead of writing plaintext, matching the posture of the
/// biometric and integration-credential protectors.
/// </summary>
public interface IBackupArtifactProtector
{
    bool IsConfigured { get; }

    string? ActiveKeyVersion { get; }

    EncryptedBackupEnvelope Seal(byte[] plaintext);

    byte[] Open(EncryptedBackupEnvelope envelope, string keyVersion);
}
