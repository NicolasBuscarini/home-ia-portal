namespace GarageWatch.Application.Identity;

/// <summary>
/// Human review state of one immutable identity observation. The original prediction is never
/// rewritten; reviews add an append-only decision and only update this projection.
/// </summary>
public enum IdentityObservationReviewStatus
{
    NotRequired,
    Pending,
    Reviewed
}

public enum IdentityReviewDecision
{
    Confirmed,
    Rejected,
    Inconclusive,
    Associated
}

public static class IdentityObservationScopes
{
    /// <summary>Narrow inbound scope for the monitoring producer; it cannot write platform events.</summary>
    public const string Write = "identity.observations.write";
}

public sealed record IdentityObservationCandidate(string PersonPublicId, double Score);

public sealed record IdentityObservationSummary(
    string PublicId,
    string? CameraPublicId,
    string? ClipPublicId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset IngestedAtUtc,
    string MatchStatus,
    string? BestPersonPublicId,
    double? BestScore,
    double? Margin,
    string PolicyVersion,
    int CandidateCount,
    string ReviewStatus,
    string? ResolvedPersonPublicId,
    string? LatestReviewPublicId);

public sealed record IdentityObservationReviewItem(
    string PublicId,
    string Decision,
    string? SelectedPersonPublicId,
    string? Note,
    string ReviewerPublicId,
    DateTimeOffset CreatedAtUtc);

public sealed record IdentityObservationDetail(
    IdentityObservationSummary Summary,
    IReadOnlyList<IdentityObservationCandidate> Candidates,
    IReadOnlyList<IdentityObservationReviewItem> Reviews);

public sealed record IdentityObservationPage(
    IReadOnlyList<IdentityObservationSummary> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record IdentityObservationIngestResult(IdentityObservationDetail Observation, bool Duplicate);

public sealed class IdentityObservationNotFoundException(string code) : Exception(code);

public sealed class IdentityObservationConflictException(string code) : Exception(code);

public static class IdentityTriageLabels
{
    public static string MatchStatus(IdentityMatchStatus status) => status switch
    {
        IdentityMatchStatus.Suggested => "suggested",
        IdentityMatchStatus.Ambiguous => "ambiguous",
        IdentityMatchStatus.Unknown => "unknown",
        _ => "noEnrollment"
    };

    public static bool TryParseMatchStatus(string? value, out IdentityMatchStatus status)
    {
        status = default;
        return value switch
        {
            "suggested" => Set(IdentityMatchStatus.Suggested, out status),
            "ambiguous" => Set(IdentityMatchStatus.Ambiguous, out status),
            "unknown" => Set(IdentityMatchStatus.Unknown, out status),
            "noEnrollment" or "no-enrollment" => Set(IdentityMatchStatus.NoEnrollment, out status),
            _ => false
        };

        static bool Set(IdentityMatchStatus value, out IdentityMatchStatus target)
        {
            target = value;
            return true;
        }
    }

    public static string ReviewStatus(IdentityObservationReviewStatus status) => status switch
    {
        IdentityObservationReviewStatus.Pending => "pending",
        IdentityObservationReviewStatus.Reviewed => "reviewed",
        _ => "notRequired"
    };

    public static bool TryParseReviewStatus(string? value, out IdentityObservationReviewStatus status)
    {
        status = default;
        return value switch
        {
            "pending" => Set(IdentityObservationReviewStatus.Pending, out status),
            "reviewed" => Set(IdentityObservationReviewStatus.Reviewed, out status),
            "notRequired" or "not-required" => Set(IdentityObservationReviewStatus.NotRequired, out status),
            _ => false
        };

        static bool Set(IdentityObservationReviewStatus value, out IdentityObservationReviewStatus target)
        {
            target = value;
            return true;
        }
    }

    public static string Decision(IdentityReviewDecision decision) => decision switch
    {
        IdentityReviewDecision.Confirmed => "confirmed",
        IdentityReviewDecision.Rejected => "rejected",
        IdentityReviewDecision.Inconclusive => "inconclusive",
        _ => "associated"
    };

    public static bool TryParseDecision(string? value, out IdentityReviewDecision decision)
    {
        decision = default;
        return value switch
        {
            "confirmed" => Set(IdentityReviewDecision.Confirmed, out decision),
            "rejected" => Set(IdentityReviewDecision.Rejected, out decision),
            "inconclusive" => Set(IdentityReviewDecision.Inconclusive, out decision),
            "associated" => Set(IdentityReviewDecision.Associated, out decision),
            _ => false
        };

        static bool Set(IdentityReviewDecision value, out IdentityReviewDecision target)
        {
            target = value;
            return true;
        }
    }
}
