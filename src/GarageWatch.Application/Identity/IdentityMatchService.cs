namespace GarageWatch.Application.Identity;

/// <summary>
/// Composes the embedding source with the pure matcher and guarantees that decrypted vectors are
/// cleared from memory after scoring. It does not persist observations, suggestions or reviews.
/// </summary>
public sealed class IdentityMatchService(IIdentityEmbeddingSource source, IIdentityMatcher matcher,
    IIdentityMatchPolicySource policies)
{
    public async Task<IdentityMatchSuggestion> MatchAsync(FaceObservationV1 observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!observation.IsValid) throw new ArgumentException("invalid_face_observation", nameof(observation));

        var policy = await policies.GetActiveAsync(cancellationToken);
        var snapshot = await source.LoadAsync(observation, cancellationToken);
        try
        {
            var result = matcher.Match(observation, snapshot.Candidates, policy);
            return result with
            {
                SkippedEnrollments = result.SkippedEnrollments + snapshot.SkippedEnrollments
            };
        }
        finally
        {
            foreach (var candidate in snapshot.Candidates)
                Array.Clear(candidate.Values);
        }
    }
}
