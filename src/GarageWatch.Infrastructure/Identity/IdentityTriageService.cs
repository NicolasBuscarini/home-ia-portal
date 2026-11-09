using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GarageWatch.Application.Identity;
using GarageWatch.Application.PlatformEvents;
using Microsoft.EntityFrameworkCore;
using CandidateContract = GarageWatch.Application.Identity.IdentityObservationCandidate;

namespace GarageWatch.Infrastructure.Identity;

/// <summary>
/// Owns the identity triage lifecycle: idempotent observation ingestion, the immutable prediction and
/// append-only human reviews. Embedding values are scored in memory and never persisted.
/// </summary>
public sealed class IdentityTriageService(
    IamDbContext database,
    IdentityMatchService matching,
    IIamUnitOfWork unitOfWork,
    IPublicIdGenerator publicIds,
    IPlatformEventRecorder recorder,
    TimeProvider timeProvider)
{
    private const int MaximumPersistedCandidates = 10;
    private const int MaximumNoteLength = 500;

    public async Task<IdentityObservationIngestResult> IngestAsync(FaceObservationV1 observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!observation.IsValid) throw new ArgumentException("invalid_face_observation", nameof(observation));

        var hash = ComputeHash(observation);
        var existing = await database.IdentityObservations.AsNoTracking()
            .Include(item => item.Candidates)
            .Include(item => item.Reviews)
            .SingleOrDefaultAsync(item => item.ObservationHash == hash, cancellationToken);
        if (existing is not null)
            return new(MapDetail(existing), Duplicate: true);

        var suggestion = await matching.MatchAsync(observation, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var observationRow = new IdentityObservation
        {
            Id = Guid.NewGuid(),
            PublicId = publicIds.Generate(PublicIdPrefixes.IdentityObservationPrefix),
            ObservationHash = hash,
            SchemaVersion = observation.SchemaVersion,
            ModelId = observation.ModelId,
            ModelVersion = observation.ModelVersion,
            EmbeddingSchemaVersion = observation.EmbeddingSchemaVersion,
            Dimensions = observation.Dimensions,
            CameraPublicId = observation.CameraPublicId,
            ClipPublicId = observation.ClipPublicId,
            ObservedAtUtc = (observation.ObservedAtUtc ?? now).ToUniversalTime(),
            IngestedAtUtc = now,
            MatchStatus = (int)suggestion.Status,
            BestPersonPublicId = suggestion.Best?.PersonPublicId,
            BestScore = suggestion.Best?.Score,
            CandidateMargin = suggestion.Margin,
            EvaluatedEnrollments = suggestion.EvaluatedEnrollments,
            SkippedEnrollments = suggestion.SkippedEnrollments,
            PolicyVersion = suggestion.Policy.Version,
            PolicyThreshold = suggestion.Policy.Threshold,
            PolicyMargin = suggestion.Policy.Margin,
            ReviewStatus = (int)(suggestion.Status is IdentityMatchStatus.Suggested or IdentityMatchStatus.Ambiguous
                ? IdentityObservationReviewStatus.Pending
                : IdentityObservationReviewStatus.NotRequired),
            Version = 1
        };
        observationRow.Candidates = suggestion.Candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.PersonPublicId, StringComparer.Ordinal)
            .Take(MaximumPersistedCandidates)
            .Select((candidate, index) => new IdentityObservationCandidate
            {
                Id = Guid.NewGuid(),
                ObservationId = observationRow.Id,
                PersonPublicId = candidate.PersonPublicId,
                Score = candidate.Score,
                Rank = index + 1
            }).ToList();

        database.IdentityObservations.Add(observationRow);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            var concurrent = await database.IdentityObservations.AsNoTracking()
                .Include(item => item.Candidates)
                .Include(item => item.Reviews)
                .SingleOrDefaultAsync(item => item.ObservationHash == hash, cancellationToken);
            if (concurrent is null) throw;
            return new(MapDetail(concurrent), Duplicate: true);
        }

        return new(MapDetail(observationRow), Duplicate: false);
    }

    public async Task<IdentityObservationPage> ListAsync(IdentityMatchStatus? matchStatus,
        IdentityObservationReviewStatus? reviewStatus, string? cameraPublicId, string? clipPublicId, int page,
        int pageSize, CancellationToken cancellationToken)
    {
        if (page < 1 || pageSize is < 1 or > 100) throw new ArgumentException("invalid_pagination");
        if (cameraPublicId is not null &&
            !PublicIdFormat.IsValid(cameraPublicId, PublicIdPrefixes.CameraPrefix))
            throw new ArgumentException("invalid_camera");
        if (clipPublicId is not null &&
            !PublicIdFormat.IsValid(clipPublicId, PublicIdPrefixes.ClipPrefix))
            throw new ArgumentException("invalid_clip");

        var query = database.IdentityObservations.AsNoTracking();
        if (matchStatus is { } match) query = query.Where(item => item.MatchStatus == (int)match);
        if (reviewStatus is { } review) query = query.Where(item => item.ReviewStatus == (int)review);
        if (cameraPublicId is not null) query = query.Where(item => item.CameraPublicId == cameraPublicId);
        if (clipPublicId is not null) query = query.Where(item => item.ClipPublicId == clipPublicId);

        var total = await query.CountAsync(cancellationToken);
        var offset = ((long)page - 1) * pageSize;
        var rows = offset >= total
            ? []
            : await query.OrderByDescending(item => item.IngestedAtUtc).ThenByDescending(item => item.Id)
                .Skip((int)offset).Take(pageSize)
                .Select(item => new
                {
                    item.PublicId,
                    item.CameraPublicId,
                    item.ClipPublicId,
                    item.ObservedAtUtc,
                    item.IngestedAtUtc,
                    MatchStatus = (IdentityMatchStatus)item.MatchStatus,
                    item.BestPersonPublicId,
                    item.BestScore,
                    item.CandidateMargin,
                    item.PolicyVersion,
                    CandidateCount = item.Candidates.Count,
                    ReviewStatus = (IdentityObservationReviewStatus)item.ReviewStatus,
                    item.ResolvedPersonPublicId,
                    item.LatestReviewPublicId
                })
                .ToArrayAsync(cancellationToken);
        var items = rows.Select(item => new IdentityObservationSummary(
            item.PublicId!, item.CameraPublicId, item.ClipPublicId, item.ObservedAtUtc, item.IngestedAtUtc,
            IdentityTriageLabels.MatchStatus(item.MatchStatus), item.BestPersonPublicId, item.BestScore,
            item.CandidateMargin, item.PolicyVersion, item.CandidateCount,
            IdentityTriageLabels.ReviewStatus(item.ReviewStatus), item.ResolvedPersonPublicId,
            item.LatestReviewPublicId)).ToArray();
        return new(items, page, pageSize, total);
    }

    public async Task<IdentityObservationDetail> GetAsync(string publicId, CancellationToken cancellationToken)
    {
        var observation = await LoadAsync(publicId, cancellationToken);
        return MapDetail(observation);
    }

    public async Task<IdentityObservationReviewItem> ReviewAsync(Guid reviewerUserId, string reviewerPublicId,
        string observationPublicId, IdentityReviewDecision decision, string? personPublicId, string? note,
        CancellationToken cancellationToken)
    {
        var person = string.IsNullOrWhiteSpace(personPublicId) ? null : personPublicId.Trim();
        var reviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (reviewNote is { Length: > MaximumNoteLength }) throw new ArgumentException("invalid_review_note");
        if (person is not null && !PublicIdFormat.IsValid(person, PublicIdPrefixes.KnownPersonPrefix))
            throw new ArgumentException("invalid_identity_review_person");

        return await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            var observation = await database.IdentityObservations
                .Include(item => item.Candidates)
                .Include(item => item.Reviews)
                .SingleOrDefaultAsync(item => item.PublicId == observationPublicId, token)
                ?? throw new IdentityObservationNotFoundException("identity_observation_not_found");

            if (observation.ReviewStatus != (int)IdentityObservationReviewStatus.Pending)
                throw new IdentityObservationConflictException("identity_observation_not_pending_review");

            switch (decision)
            {
                case IdentityReviewDecision.Confirmed when person is null ||
                    observation.Candidates.All(candidate => candidate.PersonPublicId != person):
                    throw new ArgumentException("invalid_identity_review_person");
                case IdentityReviewDecision.Associated when person is null:
                    throw new ArgumentException("invalid_identity_review_person");
                case IdentityReviewDecision.Associated:
                    if (!await database.KnownPeople.AnyAsync(
                            known => known.PublicId == person && known.DeletedAtUtc == null, token))
                        throw new ArgumentException("invalid_identity_review_person");
                    break;
                case IdentityReviewDecision.Rejected or IdentityReviewDecision.Inconclusive when person is not null:
                    throw new ArgumentException("invalid_identity_review_person");
            }

            var now = timeProvider.GetUtcNow();
            var review = new IdentityObservationReview
            {
                Id = Guid.NewGuid(),
                PublicId = publicIds.Generate(PublicIdPrefixes.IdentityReviewPrefix),
                ObservationId = observation.Id,
                ReviewerUserId = reviewerUserId,
                ReviewerPublicId = reviewerPublicId,
                Decision = (int)decision,
                SelectedPersonPublicId = person,
                Note = reviewNote,
                CreatedAtUtc = now
            };
            database.IdentityObservationReviews.Add(review);

            observation.ReviewStatus = (int)IdentityObservationReviewStatus.Reviewed;
            observation.ResolvedPersonPublicId = person;
            observation.LatestReviewPublicId = review.PublicId;
            observation.Version++;

            await recorder.RecordRequiredAsync(new PlatformEventDraft(
                "identity.observation.reviewed", PlatformEventOutcomes.Success,
                Resource: new PlatformEventReferenceV1("identity_observation", observationPublicId),
                Metadata: new Dictionary<string, object?>
                {
                    ["decision"] = IdentityTriageLabels.Decision(decision),
                    ["matchStatus"] = IdentityTriageLabels.MatchStatus((IdentityMatchStatus)observation.MatchStatus),
                    ["policyVersion"] = observation.PolicyVersion,
                    ["personPublicId"] = person
                },
                OccurredAtUtc: now), token);

            return new IdentityObservationReviewItem(review.PublicId!, IdentityTriageLabels.Decision(decision),
                person, reviewNote, reviewerPublicId, now);
        }, cancellationToken);
    }

    private async Task<IdentityObservation> LoadAsync(string publicId, CancellationToken cancellationToken)
    {
        var observation = await database.IdentityObservations.AsNoTracking()
            .Include(item => item.Candidates)
            .Include(item => item.Reviews)
            .SingleOrDefaultAsync(item => item.PublicId == publicId, cancellationToken);
        return observation ?? throw new IdentityObservationNotFoundException("identity_observation_not_found");
    }

    private static IdentityObservationDetail MapDetail(IdentityObservation observation) => new(
        new IdentityObservationSummary(observation.PublicId!, observation.CameraPublicId,
            observation.ClipPublicId, observation.ObservedAtUtc, observation.IngestedAtUtc,
            IdentityTriageLabels.MatchStatus((IdentityMatchStatus)observation.MatchStatus),
            observation.BestPersonPublicId, observation.BestScore, observation.CandidateMargin,
            observation.PolicyVersion, observation.Candidates.Count,
            IdentityTriageLabels.ReviewStatus((IdentityObservationReviewStatus)observation.ReviewStatus),
            observation.ResolvedPersonPublicId, observation.LatestReviewPublicId),
        observation.Candidates.OrderBy(candidate => candidate.Rank)
            .Select(candidate => new CandidateContract(candidate.PersonPublicId, candidate.Score))
            .ToArray(),
        observation.Reviews.OrderBy(review => review.CreatedAtUtc).ThenBy(review => review.Id)
            .Select(review => new IdentityObservationReviewItem(review.PublicId!,
                IdentityTriageLabels.Decision((IdentityReviewDecision)review.Decision),
                review.SelectedPersonPublicId, review.Note, review.ReviewerPublicId, review.CreatedAtUtc))
            .ToArray());

    /// <summary>
    /// Canonical digest used only for idempotency. It is a one-way fingerprint of the observation,
    /// not a reversible encoding of the biometric vector.
    /// </summary>
    private static string ComputeHash(FaceObservationV1 observation)
    {
        var builder = new StringBuilder();
        builder.Append(observation.SchemaVersion).Append('\u001f')
            .Append(observation.ModelId).Append('\u001f')
            .Append(observation.ModelVersion).Append('\u001f')
            .Append(observation.EmbeddingSchemaVersion).Append('\u001f')
            .Append(observation.Dimensions.ToString(CultureInfo.InvariantCulture)).Append('\u001f');
        foreach (var value in observation.Values)
            builder.Append(BitConverter.SingleToInt32Bits(value).ToString("x8", CultureInfo.InvariantCulture));
        builder.Append('\u001f').Append(observation.CameraPublicId).Append('\u001f')
            .Append(observation.ClipPublicId).Append('\u001f')
            .Append(observation.ObservedAtUtc?.ToUniversalTime().UtcTicks.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
