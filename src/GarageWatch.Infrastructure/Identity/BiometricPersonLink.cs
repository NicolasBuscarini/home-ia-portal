namespace GarageWatch.Infrastructure.Identity;

/// <summary>
/// Portal-owned association between one encrypted biometric enrollment and one authorized roster person.
/// The biometric payload stays in the enrollment store; this row only links non-biometric public IDs and
/// keeps the prior association as a tombstone when it is replaced or removed.
/// </summary>
public sealed class BiometricPersonLink
{
    public Guid Id { get; set; }
    public string EnrollmentPublicId { get; set; } = string.Empty;
    public string KnownPersonPublicId { get; set; } = string.Empty;
    public Guid LinkedByUserId { get; set; }
    public DateTimeOffset LinkedAtUtc { get; set; }
    public DateTimeOffset? UnlinkedAtUtc { get; set; }
}
