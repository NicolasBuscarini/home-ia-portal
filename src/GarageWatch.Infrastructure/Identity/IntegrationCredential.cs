namespace GarageWatch.Infrastructure.Identity;

/// <summary>Encrypted integration credential stored in the portal database (P32).</summary>
public sealed class IntegrationCredential
{
    public Guid Id { get; set; }
    public string IntegrationId { get; set; } = string.Empty;
    public string KeyVersion { get; set; } = string.Empty;
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Tag { get; set; } = [];
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    public ApplicationUser? UpdatedBy { get; set; }
}
