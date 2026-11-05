namespace GarageWatch.Application.Identity;

/// <summary>
/// Bounded window over reviewed observations. Calibration never exports media, embeddings or
/// observation identifiers; it derives aggregate score histograms only (ADR121).
/// </summary>
public sealed record IdentityCalibrationQuery(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string? CameraPublicId);

public static class IdentityCalibrationRules
{
    /// <summary>Below this count on either class a policy cannot be promoted.</summary>
    public const int MinimumSamplesPerClass = 30;

    public const int DefaultBins = 50;
    public const int MinimumBins = 10;
    public const int MaximumBins = 200;
    public const int MaximumWindowDays = 365;
    public const int MaximumSamples = 20_000;

    public static bool IsValidWindow(DateTimeOffset from, DateTimeOffset to) =>
        from < to && to - from <= TimeSpan.FromDays(MaximumWindowDays);

    public static bool IsValidBins(int bins) => bins is >= MinimumBins and <= MaximumBins;
}

/// <summary>One bucket of a bounded 0..1 histogram, split by ground-truth class.</summary>
public sealed record IdentityCalibrationBin(
    double LowerBound, double UpperBound, int GenuineCount, int ImpostorCount);

/// <summary>
/// Aggregate view of the reviewed sample at a point in time. It carries the current policy, the
/// sample counts, a digest for draft/snapshot consistency and the score/gap histograms; the client
/// recomputes metrics for each slider position without receiving individual samples.
/// </summary>
public sealed record IdentityCalibrationSnapshot(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? CameraPublicId,
    int Bins,
    string SnapshotHash,
    IdentityMatchPolicy ActivePolicy,
    int GenuineCount,
    int ImpostorCount,
    int ExcludedCount,
    int SingleCandidateCount,
    int MinimumSamplesPerClass,
    bool CanPromote,
    IReadOnlyList<IdentityCalibrationBin> ScoreBins,
    IReadOnlyList<IdentityCalibrationBin> MarginBins);

public sealed record IdentityMatchPolicyDraftRequest(
    double Threshold,
    double Margin,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? CameraPublicId,
    string? SnapshotHash);

public enum IdentityMatchPolicyStatus
{
    Draft,
    Active,
    Archived
}

public sealed record IdentityMatchPolicyVersionItem(
    string PublicId,
    int Version,
    double Threshold,
    double Margin,
    string Status,
    string SnapshotHash,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? CameraPublicId,
    int GenuineCount,
    int ImpostorCount,
    int ExcludedCount,
    double? EstimatedFar,
    double? EstimatedFrr,
    string CreatedByPublicId,
    DateTimeOffset CreatedAtUtc,
    string? PromotedByPublicId,
    DateTimeOffset? PromotedAtUtc);

/// <summary>Resolves the policy applied to new observations; baseline when nothing was promoted.</summary>
public interface IIdentityMatchPolicySource
{
    Task<IdentityMatchPolicy> GetActiveAsync(CancellationToken cancellationToken = default);
}

public sealed class IdentityMatchPolicyNotFoundException(string code) : Exception(code);

public sealed class IdentityCalibrationConflictException(string code) : Exception(code);

public static class IdentityMatchPolicyLabels
{
    public static string Status(IdentityMatchPolicyStatus status) => status switch
    {
        IdentityMatchPolicyStatus.Active => "active",
        IdentityMatchPolicyStatus.Archived => "archived",
        _ => "draft"
    };
}
