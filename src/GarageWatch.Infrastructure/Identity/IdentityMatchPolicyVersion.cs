namespace GarageWatch.Infrastructure.Identity;

/// <summary>
/// Versioned threshold/margin policy produced by the interactive calibration screen (ADR121).
/// Promotion is explicit and audited; only one row is active and archived rows remain as rollback.
/// </summary>
public sealed class IdentityMatchPolicyVersion : IHasPublicId
{
    public Guid Id { get; set; }
    public string? PublicId { get; set; }
    public int Version { get; set; }
    public double Threshold { get; set; }
    public double Margin { get; set; }
    public int Status { get; set; }
    public string SnapshotHash { get; set; } = string.Empty;
    public DateTimeOffset FromUtc { get; set; }
    public DateTimeOffset ToUtc { get; set; }
    public string? CameraPublicId { get; set; }
    public int GenuineCount { get; set; }
    public int ImpostorCount { get; set; }
    public int ExcludedCount { get; set; }
    public double? EstimatedFar { get; set; }
    public double? EstimatedFrr { get; set; }
    public string CreatedByPublicId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? PromotedByPublicId { get; set; }
    public DateTimeOffset? PromotedAtUtc { get; set; }
}
