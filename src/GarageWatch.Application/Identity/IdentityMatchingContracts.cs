namespace GarageWatch.Application.Identity;

/// <summary>
/// One face observation submitted for matching. It carries only a derived embedding and opaque
/// context references; images, crops and person names are deliberately absent (ADR113).
/// </summary>
public sealed record FaceObservationV1(
    string SchemaVersion,
    string ModelId,
    string ModelVersion,
    string EmbeddingSchemaVersion,
    int Dimensions,
    IReadOnlyList<float> Values,
    string? CameraPublicId = null,
    string? ClipPublicId = null,
    DateTimeOffset? ObservedAtUtc = null)
{
    public const string CurrentSchemaVersion = "face-observation.v1";
    public const string CurrentEmbeddingSchemaVersion = "face-embedding.v1";

    public bool IsValid => SchemaVersion == CurrentSchemaVersion &&
        !string.IsNullOrWhiteSpace(ModelId) && ModelId.Length <= 128 &&
        !string.IsNullOrWhiteSpace(ModelVersion) && ModelVersion.Length <= 128 &&
        EmbeddingSchemaVersion == CurrentEmbeddingSchemaVersion &&
        Dimensions is > 0 and <= 4096 &&
        Values is not null && Values.Count == Dimensions &&
        Values.All(float.IsFinite);
}

/// <summary>Versioned threshold/margin pair. The baseline is not a calibration (ADR075/ADR113).</summary>
public sealed record IdentityMatchPolicy(double Threshold, double Margin, string Version)
{
    public const string BaselineVersion = "identity-match.baseline.v1";
    public const double BaselineThreshold = 0.363;
    public const double BaselineMargin = 0.05;

    public static IdentityMatchPolicy Baseline { get; } =
        new(BaselineThreshold, BaselineMargin, BaselineVersion);

    public bool IsValid => Threshold is >= 0 and <= 1 && Margin is >= 0 and <= 1 &&
        !string.IsNullOrWhiteSpace(Version) && Version.Length <= 64;
}

public enum IdentityMatchStatus
{
    Suggested,
    Ambiguous,
    Unknown,
    NoEnrollment
}

/// <summary>Score for one authorized roster person; the original prediction is never rewritten.</summary>
public sealed record IdentityMatchCandidate(string PersonPublicId, double Score);

public sealed record IdentityMatchSuggestion(
    IdentityMatchStatus Status,
    IdentityMatchCandidate? Best,
    double? Margin,
    IReadOnlyList<IdentityMatchCandidate> Candidates,
    int EvaluatedEnrollments,
    int SkippedEnrollments,
    IdentityMatchPolicy Policy);

/// <summary>One decrypted candidate produced by the embedding source. Plaintext lives only in memory.</summary>
public sealed record IdentityEmbeddingCandidate(
    string PersonPublicId,
    string EnrollmentPublicId,
    string ModelId,
    string ModelVersion,
    string SchemaVersion,
    int Dimensions,
    float[] Values);

public sealed record IdentityEmbeddingSnapshot(
    IReadOnlyList<IdentityEmbeddingCandidate> Candidates,
    int SkippedEnrollments);

/// <summary>Boundary that resolves active, person-linked enrollments into comparable embeddings.</summary>
public interface IIdentityEmbeddingSource
{
    Task<IdentityEmbeddingSnapshot> LoadAsync(FaceObservationV1 observation,
        CancellationToken cancellationToken = default);
}

/// <summary>Pure scoring boundary; it performs no I/O and never persists embeddings.</summary>
public interface IIdentityMatcher
{
    IdentityMatchSuggestion Match(FaceObservationV1 observation,
        IReadOnlyList<IdentityEmbeddingCandidate> candidates, IdentityMatchPolicy? policy = null);
}

/// <summary>Matching is deliberately unavailable when the embedding source or protection is missing.</summary>
public sealed class IdentityMatchingUnavailableException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
