using System.Security.Cryptography;
using System.Text;
using GarageWatch.Application.Portal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Integrations;

/// <summary>
/// AES-256-GCM protector for portal-owned integration credentials. It keeps the active key and an
/// optional previous key so a configured rotation can decrypt and re-seal legacy rows. A missing or
/// invalid key disables writes instead of storing plaintext.
/// </summary>
public sealed class AesGcmIntegrationCredentialProtector : IIntegrationCredentialProtector
{
    public const string KeySetting = "CREDENTIAL_ENCRYPTION_KEY";
    public const string KeyVersionSetting = "CREDENTIAL_ENCRYPTION_KEY_VERSION";
    public const string PreviousKeySetting = "CREDENTIAL_ENCRYPTION_PREVIOUS_KEY";
    public const string PreviousKeyVersionSetting = "CREDENTIAL_ENCRYPTION_PREVIOUS_KEY_VERSION";
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private readonly ILogger<AesGcmIntegrationCredentialProtector> _logger;

    public AesGcmIntegrationCredentialProtector(IConfiguration configuration,
        ILogger<AesGcmIntegrationCredentialProtector> logger)
    {
        _logger = logger;
        ActiveKeyVersion = ReadVersion(configuration, KeyVersionSetting);
        var active = ReadKey(configuration, KeySetting);
        if (active is not null && ActiveKeyVersion is not null)
            _keys[ActiveKeyVersion] = active;

        var previousVersion = ReadVersion(configuration, PreviousKeyVersionSetting);
        var previous = ReadKey(configuration, PreviousKeySetting);
        if (previous is not null && previousVersion is not null && previousVersion != ActiveKeyVersion)
            _keys[previousVersion] = previous;

        if (_keys.Count == 0)
            _logger.LogWarning("Credenciais de integração desabilitadas: chave de criptografia ausente ou inválida.");
    }

    public bool IsConfigured => ActiveKeyVersion is not null && _keys.ContainsKey(ActiveKeyVersion);

    public string? ActiveKeyVersion { get; }

    public bool SupportsKeyVersion(string keyVersion) => _keys.ContainsKey(keyVersion);

    public SealedIntegrationCredential Seal(string plaintext, string associatedData)
    {
        if (ActiveKeyVersion is null || !_keys.TryGetValue(ActiveKeyVersion, out var key))
            throw new IntegrationCredentialEncryptionUnavailableException();
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, Encoding.UTF8.GetBytes(associatedData));
        return new(ciphertext, nonce, tag, ActiveKeyVersion);
    }

    public string? TryOpen(byte[] ciphertext, byte[] nonce, byte[] tag, string keyVersion,
        string associatedData)
    {
        if (!_keys.TryGetValue(keyVersion, out var key) || nonce.Length != NonceBytes || tag.Length != TagBytes)
            return null;
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(associatedData));
        }
        catch (CryptographicException)
        {
            return null;
        }
        return Encoding.UTF8.GetString(plaintext);
    }

    private byte[]? ReadKey(IConfiguration configuration, string setting)
    {
        var raw = configuration[setting]?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var decoded = Convert.FromBase64String(raw);
            if (decoded.Length == KeyBytes) return decoded;
            _logger.LogError("Chave de credencial inválida: {Setting} deve ter {Bytes} bytes.", setting, KeyBytes);
        }
        catch (FormatException)
        {
            _logger.LogError("Chave de credencial inválida: {Setting} não é base64.", setting);
        }
        return null;
    }

    private string? ReadVersion(IConfiguration configuration, string setting)
    {
        var raw = configuration[setting]?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        if (raw.Length > 32 || raw.Any(char.IsControl))
        {
            _logger.LogError("Versão de chave inválida em {Setting}.", setting);
            return null;
        }
        return raw;
    }
}
