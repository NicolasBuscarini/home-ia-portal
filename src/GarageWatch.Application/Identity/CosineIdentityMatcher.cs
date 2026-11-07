namespace GarageWatch.Application.Identity;

/// <summary>
/// Cosine matching over already-decrypted embeddings. Only candidates produced by the same model,
/// schema and dimensions are compared; aggregation takes the best score per authorized person.
/// </summary>
public sealed class CosineIdentityMatcher : IIdentityMatcher
{
    private const int MaximumCandidates = 5;

    public IdentityMatchSuggestion Match(FaceObservationV1 observation,
        IReadOnlyList<IdentityEmbeddingCandidate> candidates, IdentityMatchPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(candidates);
        if (!observation.IsValid) throw new ArgumentException("invalid_face_observation", nameof(observation));
        var activePolicy = policy ?? IdentityMatchPolicy.Baseline;
        if (!activePolicy.IsValid) throw new ArgumentException("invalid_identity_match_policy", nameof(policy));

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var evaluated = 0;
        var skipped = 0;
        foreach (var candidate in candidates)
        {
            if (!IsComparable(observation, candidate))
            {
                skipped++;
                continue;
            }
            var score = IdentitySimilarity.Cosine(observation.Values, candidate.Values);
            if (!double.IsFinite(score))
            {
                skipped++;
                continue;
            }
            evaluated++;
            if (!scores.TryGetValue(candidate.PersonPublicId, out var current) || score > current)
                scores[candidate.PersonPublicId] = score;
        }

        var ordered = scores
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new IdentityMatchCandidate(pair.Key, pair.Value))
            .ToArray();
        if (ordered.Length == 0)
            return new(IdentityMatchStatus.NoEnrollment, null, null, [], evaluated, skipped, activePolicy);

        var best = ordered[0];
        var second = ordered.Length > 1 ? ordered[1] : null;
        var margin = best.Score - (second?.Score ?? 0);
        var status = best.Score < activePolicy.Threshold
            ? IdentityMatchStatus.Unknown
            : margin < activePolicy.Margin
                ? IdentityMatchStatus.Ambiguous
                : IdentityMatchStatus.Suggested;
        return new(status, status == IdentityMatchStatus.Unknown ? null : best, margin,
            ordered.Take(MaximumCandidates).ToArray(), evaluated, skipped, activePolicy);
    }

    private static bool IsComparable(FaceObservationV1 observation, IdentityEmbeddingCandidate candidate) =>
        string.Equals(candidate.ModelId, observation.ModelId, StringComparison.Ordinal) &&
        string.Equals(candidate.ModelVersion, observation.ModelVersion, StringComparison.Ordinal) &&
        string.Equals(candidate.SchemaVersion, observation.EmbeddingSchemaVersion, StringComparison.Ordinal) &&
        candidate.Dimensions == observation.Dimensions &&
        candidate.Values.Length == observation.Dimensions;

}
