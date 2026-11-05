using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GarageWatch.Application.Identity;
using GarageWatch.Application.PlatformEvents;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

/// <summary>
/// Derives calibration aggregates from reviewed observations and manages the versioned policy
/// lifecycle (draft, explicit promotion, rollback). It never reads or returns media, embeddings or
/// observation identifiers: samples are reduced to score/gap histograms and counts (ADR121).
/// </summary>
public sealed class IdentityCalibrationService(
    IamDbContext database,
    IPublicIdGenerator publicIds,
    IIamUnitOfWork unitOfWork,
    IPlatformEventRecorder recorder,
    IIdentityMatchPolicySource policies,
    TimeProvider timeProvider)
{
    public async Task<IdentityCalibrationSnapshot> GetSnapshotAsync(IdentityCalibrationQuery query, int bins,
        CancellationToken cancellationToken)
    {
        Validate(query);
        if (!IdentityCalibrationRules.IsValidBins(bins))
            throw new ArgumentException("invalid_calibration_bins");

        var sample = await LoadSampleAsync(query, cancellationToken);
        return MapSnapshot(query, bins, sample, await policies.GetActiveAsync(cancellationToken));
    }

    public async Task<IdentityMatchPolicyVersionItem> CreateDraftAsync(string actorPublicId,
        IdentityMatchPolicyDraftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = new IdentityCalibrationQuery(request.FromUtc, request.ToUtc, request.CameraPublicId);
        Validate(query);
        var policy = new IdentityMatchPolicy(request.Threshold, request.Margin, "draft");
        if (!policy.IsValid) throw new ArgumentException("invalid_identity_match_policy");

        var sample = await LoadSampleAsync(query, cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.SnapshotHash) &&
            !string.Equals(request.SnapshotHash, sample.SnapshotHash, StringComparison.Ordinal))
            throw new IdentityCalibrationConflictException("calibration_snapshot_changed");
        if (sample.GenuineScores.Count < IdentityCalibrationRules.MinimumSamplesPerClass ||
            sample.ImpostorScores.Count < IdentityCalibrationRules.MinimumSamplesPerClass)
            throw new IdentityCalibrationConflictException("insufficient_calibration_sample");

        var (far, frr) = EstimateErrors(sample, policy.Threshold);
        var now = timeProvider.GetUtcNow();

        return await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            var nextVersion = (await database.IdentityMatchPolicyVersions
                .MaxAsync(version => (int?)version.Version, token) ?? 0) + 1;
            var row = new IdentityMatchPolicyVersion
            {
                Id = Guid.NewGuid(),
                PublicId = publicIds.Generate(PublicIdPrefixes.IdentityMatchPolicyPrefix),
                Version = nextVersion,
                Threshold = policy.Threshold,
                Margin = policy.Margin,
                Status = (int)IdentityMatchPolicyStatus.Draft,
                SnapshotHash = sample.SnapshotHash,
                FromUtc = query.FromUtc.ToUniversalTime(),
                ToUtc = query.ToUtc.ToUniversalTime(),
                CameraPublicId = query.CameraPublicId,
                GenuineCount = sample.GenuineScores.Count,
                ImpostorCount = sample.ImpostorScores.Count,
                ExcludedCount = sample.ExcludedCount,
                EstimatedFar = far,
                EstimatedFrr = frr,
                CreatedByPublicId = actorPublicId,
                CreatedAtUtc = now
            };
            database.IdentityMatchPolicyVersions.Add(row);
            await recorder.RecordRequiredAsync(new PlatformEventDraft(
                "identity.match_policy.drafted", PlatformEventOutcomes.Success,
                Resource: new PlatformEventReferenceV1("identity_match_policy", row.PublicId!),
                Metadata: new Dictionary<string, object?>
                {
                    ["threshold"] = row.Threshold,
                    ["margin"] = row.Margin,
                    ["genuineCount"] = row.GenuineCount,
                    ["impostorCount"] = row.ImpostorCount
                },
                OccurredAtUtc: now), token);
            return Map(row);
        }, cancellationToken);
    }

    public async Task<IdentityMatchPolicyVersionItem> PromoteAsync(string actorPublicId, string publicId,
        CancellationToken cancellationToken)
    {
        if (!PublicIdFormat.IsValid(publicId, PublicIdPrefixes.IdentityMatchPolicyPrefix))
            throw new ArgumentException("invalid_identity_match_policy");

        return await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            var target = await database.IdentityMatchPolicyVersions
                .SingleOrDefaultAsync(version => version.PublicId == publicId, token)
                ?? throw new IdentityMatchPolicyNotFoundException("identity_match_policy_not_found");
            if (target.Status == (int)IdentityMatchPolicyStatus.Active)
                throw new IdentityCalibrationConflictException("identity_match_policy_already_active");

            var previous = await database.IdentityMatchPolicyVersions
                .Where(version => version.Status == (int)IdentityMatchPolicyStatus.Active)
                .SingleOrDefaultAsync(token);
            var now = timeProvider.GetUtcNow();
            var rollback = target.Status == (int)IdentityMatchPolicyStatus.Archived;
            if (previous is not null)
            {
                // Archive first so the filtered unique index never sees two active rows in one batch.
                previous.Status = (int)IdentityMatchPolicyStatus.Archived;
                await database.SaveChangesAsync(token);
            }

            target.Status = (int)IdentityMatchPolicyStatus.Active;
            target.PromotedByPublicId = actorPublicId;
            target.PromotedAtUtc = now;
            await recorder.RecordRequiredAsync(new PlatformEventDraft(
                "identity.match_policy.promoted", PlatformEventOutcomes.Success,
                Resource: new PlatformEventReferenceV1("identity_match_policy", target.PublicId!),
                Metadata: new Dictionary<string, object?>
                {
                    ["version"] = target.Version,
                    ["previousVersion"] = previous?.Version,
                    ["threshold"] = target.Threshold,
                    ["margin"] = target.Margin,
                    ["rollback"] = rollback
                },
                OccurredAtUtc: now), token);
            return Map(target);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<IdentityMatchPolicyVersionItem>> ListAsync(
        CancellationToken cancellationToken) =>
        (await database.IdentityMatchPolicyVersions.AsNoTracking()
            .OrderByDescending(version => version.Version)
            .ToListAsync(cancellationToken))
        .Select(Map)
        .ToArray();

    private async Task<CalibrationSample> LoadSampleAsync(IdentityCalibrationQuery query,
        CancellationToken cancellationToken)
    {
        var from = query.FromUtc.ToUniversalTime();
        var to = query.ToUtc.ToUniversalTime();
        var camera = string.IsNullOrWhiteSpace(query.CameraPublicId) ? null : query.CameraPublicId.Trim();

        var rows = await database.IdentityObservations.AsNoTracking()
            .Where(observation => observation.ReviewStatus == (int)IdentityObservationReviewStatus.Reviewed &&
                observation.IngestedAtUtc >= from && observation.IngestedAtUtc <= to &&
                (camera == null || observation.CameraPublicId == camera))
            .OrderBy(observation => observation.IngestedAtUtc).ThenBy(observation => observation.Id)
            .Take(IdentityCalibrationRules.MaximumSamples)
            .Select(observation => new
            {
                observation.Id,
                observation.ResolvedPersonPublicId,
                Decision = observation.Reviews.OrderByDescending(review => review.CreatedAtUtc)
                    .ThenByDescending(review => review.Id)
                    .Select(review => (int?)review.Decision)
                    .FirstOrDefault(),
                Candidates = observation.Candidates.OrderByDescending(candidate => candidate.Score)
                    .Select(candidate => new { candidate.PersonPublicId, candidate.Score })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        var genuineScores = new List<double>();
        var impostorScores = new List<double>();
        var genuineMargins = new List<double>();
        var impostorMargins = new List<double>();
        var excluded = 0;
        var singleCandidate = 0;
        var hash = new StringBuilder();

        foreach (var row in rows)
        {
            var decision = row.Decision is { } value ? (IdentityReviewDecision)value : (IdentityReviewDecision?)null;
            double? genuineScore = null;
            double? impostorScore = null;

            if (decision is IdentityReviewDecision.Confirmed or IdentityReviewDecision.Associated &&
                row.ResolvedPersonPublicId is { } resolved)
            {
                foreach (var candidate in row.Candidates)
                {
                    if (!string.Equals(candidate.PersonPublicId, resolved, StringComparison.Ordinal)) continue;
                    genuineScore = candidate.Score;
                    break;
                }
            }
            else if (decision == IdentityReviewDecision.Rejected && row.Candidates.Count > 0)
            {
                impostorScore = row.Candidates[0].Score;
            }

            if (row.Candidates.Count >= 2)
            {
                var gap = row.Candidates[0].Score - row.Candidates[1].Score;
                if (genuineScore is not null) genuineMargins.Add(gap);
                else if (impostorScore is not null) impostorMargins.Add(gap);
            }
            else
            {
                singleCandidate++;
            }

            if (genuineScore is { } accepted) genuineScores.Add(accepted);
            else if (impostorScore is { } rejected) impostorScores.Add(rejected);
            else excluded++;

            hash.Append(row.Id.ToString("N")).Append('|')
                .Append(row.Decision?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('\n');
        }

        hash.Append(from.ToUnixTimeSeconds()).Append('|').Append(to.ToUnixTimeSeconds()).Append('|')
            .Append(camera ?? string.Empty);
        var snapshotHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hash.ToString())))
            .ToLowerInvariant();

        return new CalibrationSample(snapshotHash, genuineScores, impostorScores, genuineMargins,
            impostorMargins, excluded, singleCandidate);
    }

    private static IdentityCalibrationSnapshot MapSnapshot(IdentityCalibrationQuery query, int bins,
        CalibrationSample sample, IdentityMatchPolicy active) => new(
        query.FromUtc, query.ToUtc, query.CameraPublicId, bins, sample.SnapshotHash, active,
        sample.GenuineScores.Count, sample.ImpostorScores.Count, sample.ExcludedCount,
        sample.SingleCandidateCount, IdentityCalibrationRules.MinimumSamplesPerClass,
        sample.GenuineScores.Count >= IdentityCalibrationRules.MinimumSamplesPerClass &&
        sample.ImpostorScores.Count >= IdentityCalibrationRules.MinimumSamplesPerClass,
        BuildBins(bins, sample.GenuineScores, sample.ImpostorScores),
        BuildBins(bins, sample.GenuineMargins, sample.ImpostorMargins));

    private static IdentityCalibrationBin[] BuildBins(int bins, IReadOnlyList<double> genuine,
        IReadOnlyList<double> impostor)
    {
        var genuineCounts = new int[bins];
        var impostorCounts = new int[bins];
        Count(genuine, genuineCounts);
        Count(impostor, impostorCounts);
        return Enumerable.Range(0, bins)
            .Select(index => new IdentityCalibrationBin(
                Math.Round((double)index / bins, 6), Math.Round((double)(index + 1) / bins, 6),
                genuineCounts[index], impostorCounts[index]))
            .ToArray();

        static void Count(IReadOnlyList<double> values, int[] target)
        {
            foreach (var value in values)
            {
                var clamped = Math.Clamp(value, 0, 1);
                var index = Math.Min(target.Length - 1, (int)Math.Floor(clamped * target.Length));
                target[index]++;
            }
        }
    }

    private static (double Far, double Frr) EstimateErrors(CalibrationSample sample, double threshold)
    {
        var far = sample.ImpostorScores.Count == 0
            ? 0
            : sample.ImpostorScores.Count(score => score >= threshold) / (double)sample.ImpostorScores.Count;
        var frr = sample.GenuineScores.Count == 0
            ? 0
            : sample.GenuineScores.Count(score => score < threshold) / (double)sample.GenuineScores.Count;
        return (Math.Round(far, 4), Math.Round(frr, 4));
    }

    private static void Validate(IdentityCalibrationQuery query)
    {
        if (!IdentityCalibrationRules.IsValidWindow(query.FromUtc, query.ToUtc))
            throw new ArgumentException("invalid_calibration_window");
        if (query.CameraPublicId is { Length: > 0 } camera &&
            !PublicIdFormat.IsValid(camera, PublicIdPrefixes.CameraPrefix))
            throw new ArgumentException("invalid_calibration_camera");
    }

    private static IdentityMatchPolicyVersionItem Map(IdentityMatchPolicyVersion row) => new(
        row.PublicId!, row.Version, row.Threshold, row.Margin,
        IdentityMatchPolicyLabels.Status((IdentityMatchPolicyStatus)row.Status), row.SnapshotHash,
        row.FromUtc, row.ToUtc, row.CameraPublicId, row.GenuineCount, row.ImpostorCount, row.ExcludedCount,
        row.EstimatedFar, row.EstimatedFrr, row.CreatedByPublicId, row.CreatedAtUtc,
        row.PromotedByPublicId, row.PromotedAtUtc);

    private sealed record CalibrationSample(
        string SnapshotHash,
        IReadOnlyList<double> GenuineScores,
        IReadOnlyList<double> ImpostorScores,
        IReadOnlyList<double> GenuineMargins,
        IReadOnlyList<double> ImpostorMargins,
        int ExcludedCount,
        int SingleCandidateCount);
}
