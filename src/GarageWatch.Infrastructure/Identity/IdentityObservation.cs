namespace GarageWatch.Infrastructure.Identity;

/// <summary>
/// Immutable prediction of one face observation. Only the derived embedding hash, the aggregated
/// scores and opaque public references are stored; embedding values and images never reach the
/// database. Human review updates only the projection fields and appends a separate decision row.
/// </summary>
public sealed class IdentityObservation : IHasPublicId
{
    public Guid Id { get; set; }
    public string? PublicId { get; set; }
    public string ObservationHash { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public string EmbeddingSchemaVersion { get; set; } = string.Empty;
    public int Dimensions { get; set; }
    public string? CameraPublicId { get; set; }
    public string? ClipPublicId { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public DateTimeOffset IngestedAtUtc { get; set; }
    public int MatchStatus { get; set; }
    public string? BestPersonPublicId { get; set; }
    public double? BestScore { get; set; }
    public double? CandidateMargin { get; set; }
    public int EvaluatedEnrollments { get; set; }
    public int SkippedEnrollments { get; set; }
    public string PolicyVersion { get; set; } = string.Empty;
    public double PolicyThreshold { get; set; }
    public double PolicyMargin { get; set; }
    public int ReviewStatus { get; set; }
    public string? ResolvedPersonPublicId { get; set; }
    public string? LatestReviewPublicId { get; set; }
    public long Version { get; set; }
    public List<IdentityObservationCandidate> Candidates { get; set; } = [];
    public List<IdentityObservationReview> Reviews { get; set; } = [];
}

public sealed class IdentityObservationCandidate
{
    public Guid Id { get; set; }
    public Guid ObservationId { get; set; }
    public string PersonPublicId { get; set; } = string.Empty;
    public double Score { get; set; }
    public int Rank { get; set; }
}

/// <summary>Append-only human decision. It never mutates the stored prediction or a prior review.</summary>
public sealed class IdentityObservationReview : IHasPublicId
{
    public Guid Id { get; set; }
    public string? PublicId { get; set; }
    public Guid ObservationId { get; set; }
    public Guid ReviewerUserId { get; set; }
    public string ReviewerPublicId { get; set; } = string.Empty;
    public int Decision { get; set; }
    public string? SelectedPersonPublicId { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
