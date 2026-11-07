using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Application.Identity;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using CandidateRow = GarageWatch.Infrastructure.Identity.IdentityObservationCandidate;

namespace GarageWatch.Api.Tests;

public sealed class IdentityCalibrationApiTests
{
    private const string Password = "Contract-Password-123!";
    private const string GenuinePerson = "PER000000000001";
    private const string OtherPerson = "PER000000000002";

    [Fact]
    public async Task Calibration_preview_aggregates_reviewed_samples_without_exposing_identifiers()
    {
        await using var factory = new ApiFactory();
        using var admin = factory.CreateClient();
        await LoginAsync(admin, "contract-user", Password);
        await SeedAsync(factory, genuine: 35, impostor: 35, inconclusive: 2, pending: 1);

        using var response = await admin.GetAsync(
            $"/api/v1/admin/identity-match-policy/calibration?fromUtc={Escape(DateTimeOffset.UtcNow.AddHours(-2))}" +
            $"&toUtc={Escape(DateTimeOffset.UtcNow.AddHours(1))}&bins=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("OBV", text, StringComparison.Ordinal);
        Assert.DoesNotContain("personPublicId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("embedding", text, StringComparison.OrdinalIgnoreCase);

        using var body = JsonDocument.Parse(text);
        Assert.Equal(35, body.RootElement.GetProperty("genuineCount").GetInt32());
        Assert.Equal(35, body.RootElement.GetProperty("impostorCount").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("excludedCount").GetInt32());
        Assert.True(body.RootElement.GetProperty("canPromote").GetBoolean());
        Assert.Equal("identity-match.baseline.v1",
            body.RootElement.GetProperty("activePolicy").GetProperty("version").GetString());

        var scoreBins = body.RootElement.GetProperty("scoreBins").EnumerateArray().ToArray();
        Assert.Equal(50, scoreBins.Length);
        Assert.Equal(35, scoreBins.Sum(bin => bin.GetProperty("genuineCount").GetInt32()));
        Assert.Equal(35, scoreBins.Sum(bin => bin.GetProperty("impostorCount").GetInt32()));
        var marginBins = body.RootElement.GetProperty("marginBins").EnumerateArray().ToArray();
        Assert.Equal(35, marginBins.Sum(bin => bin.GetProperty("genuineCount").GetInt32()));
        Assert.Equal(35, marginBins.Sum(bin => bin.GetProperty("impostorCount").GetInt32()));
    }

    [Fact]
    public async Task Draft_requires_sample_and_promotion_applies_policy_with_rollback()
    {
        await using var factory = new ApiFactory();
        using var admin = factory.CreateClient();
        await LoginAsync(admin, "contract-user", Password);
        await SeedAsync(factory, genuine: 10, impostor: 10, inconclusive: 0, pending: 0);

        var to = DateTimeOffset.UtcNow.AddHours(1);
        var from = to.AddDays(-1);
        var window = $"fromUtc={Escape(from)}&toUtc={Escape(to)}";

        using var insufficientPreview = await admin.GetAsync(
            $"/api/v1/admin/identity-match-policy/calibration?{window}");
        using var insufficientBody = JsonDocument.Parse(
            await insufficientPreview.Content.ReadAsStringAsync());
        Assert.False(insufficientBody.RootElement.GetProperty("canPromote").GetBoolean());

        using var insufficientDraft = await admin.PostAsJsonAsync(
            "/api/v1/admin/identity-match-policy/drafts",
            new { threshold = 0.42, margin = 0.06, fromUtc = from, toUtc = to });
        Assert.Equal(HttpStatusCode.Conflict, insufficientDraft.StatusCode);
        Assert.Contains("insufficient_calibration_sample",
            await insufficientDraft.Content.ReadAsStringAsync());

        await SeedAsync(factory, genuine: 30, impostor: 30, inconclusive: 0, pending: 0);

        using var preview = await admin.GetAsync($"/api/v1/admin/identity-match-policy/calibration?{window}");
        using var previewBody = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.True(previewBody.RootElement.GetProperty("canPromote").GetBoolean());
        var snapshotHash = previewBody.RootElement.GetProperty("snapshotHash").GetString();
        Assert.False(string.IsNullOrWhiteSpace(snapshotHash));

        using var stale = await admin.PostAsJsonAsync("/api/v1/admin/identity-match-policy/drafts",
            new { threshold = 0.42, margin = 0.06, fromUtc = from, toUtc = to, snapshotHash = "stale" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("calibration_snapshot_changed", await stale.Content.ReadAsStringAsync());

        var firstId = await CreateDraftAsync(admin, from, to, snapshotHash!, threshold: 0.42, margin: 0.06);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var stored = await database.IdentityMatchPolicyVersions.SingleAsync(row => row.PublicId == firstId);
            Assert.Equal((int)IdentityMatchPolicyStatus.Draft, stored.Status);
            Assert.Equal(1, stored.Version);
            Assert.Equal(40, stored.GenuineCount);
            Assert.Equal(40, stored.ImpostorCount);
            Assert.Equal(1d, stored.EstimatedFar!.Value);
            Assert.Equal(0d, stored.EstimatedFrr!.Value);
        }

        using (var promoted = await admin.PostAsync(
            $"/api/v1/admin/identity-match-policy/{firstId}/promote", null))
        {
            Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var policies = scope.ServiceProvider.GetRequiredService<IIdentityMatchPolicySource>();
            var active = await policies.GetActiveAsync();
            Assert.Equal(0.42, active.Threshold);
            Assert.Equal(0.06, active.Margin);
            Assert.Equal("identity-match.policy.v1", active.Version);

            var matching = new IdentityMatchService(new EmptyIdentityEmbeddingSource(),
                scope.ServiceProvider.GetRequiredService<IIdentityMatcher>(), policies);
            var suggestion = await matching.MatchAsync(new FaceObservationV1(
                FaceObservationV1.CurrentSchemaVersion, "test-face-model", "contract-v1",
                FaceObservationV1.CurrentEmbeddingSchemaVersion, 3, new[] { 0.1f, 0.2f, 0.3f }));
            Assert.Equal("identity-match.policy.v1", suggestion.Policy.Version);
        }

        using var secondPreview = await admin.GetAsync(
            $"/api/v1/admin/identity-match-policy/calibration?{window}");
        using var secondPreviewBody = JsonDocument.Parse(await secondPreview.Content.ReadAsStringAsync());
        var secondHash = secondPreviewBody.RootElement.GetProperty("snapshotHash").GetString();
        var secondId = await CreateDraftAsync(admin, from, to, secondHash!, threshold: 0.6, margin: 0.02);

        using (var promoted = await admin.PostAsync(
            $"/api/v1/admin/identity-match-policy/{secondId}/promote", null))
        {
            Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        }

        using (var rolledBack = await admin.PostAsync(
            $"/api/v1/admin/identity-match-policy/{firstId}/promote", null))
        {
            Assert.Equal(HttpStatusCode.OK, rolledBack.StatusCode);
            using var rollbackBody = JsonDocument.Parse(await rolledBack.Content.ReadAsStringAsync());
            Assert.Equal("active", rollbackBody.RootElement.GetProperty("status").GetString());
        }

        using var versions = await admin.GetAsync("/api/v1/admin/identity-match-policy");
        Assert.Equal(HttpStatusCode.OK, versions.StatusCode);
        using var versionsBody = JsonDocument.Parse(await versions.Content.ReadAsStringAsync());
        var items = versionsBody.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal("active", items.Single(item => item.GetProperty("publicId").GetString() == firstId)
            .GetProperty("status").GetString());
        Assert.Equal("archived", items.Single(item => item.GetProperty("publicId").GetString() == secondId)
            .GetProperty("status").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var events = await database.PlatformEvents
                .Where(entry => entry.EventType == "identity.match_policy.promoted")
                .ToListAsync();
            Assert.Equal(3, events.Count);
            Assert.Contains(events, entry =>
                entry.MetadataJson is not null &&
                entry.MetadataJson.Contains("\"rollback\":true", StringComparison.Ordinal));
        }
    }

    private static async Task<string> CreateDraftAsync(HttpClient admin, DateTimeOffset from,
        DateTimeOffset to, string snapshotHash, double threshold, double margin)
    {
        using var draft = await admin.PostAsJsonAsync("/api/v1/admin/identity-match-policy/drafts",
            new { threshold, margin, fromUtc = from, toUtc = to, snapshotHash });
        Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        using var body = JsonDocument.Parse(await draft.Content.ReadAsStringAsync());
        var publicId = body.RootElement.GetProperty("publicId").GetString()!;
        Assert.StartsWith("MPL", publicId, StringComparison.Ordinal);
        Assert.Equal("draft", body.RootElement.GetProperty("status").GetString());
        return publicId;
    }

    private static async Task SeedAsync(WebApplicationFactory<Program> factory, int genuine, int impostor,
        int inconclusive, int pending)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var now = DateTimeOffset.UtcNow;
        var rows = new List<IdentityObservation>();

        for (var index = 0; index < genuine; index++)
            rows.Add(Observation(now, IdentityObservationReviewStatus.Reviewed,
                IdentityReviewDecision.Confirmed, GenuinePerson,
                (GenuinePerson, 0.8), (OtherPerson, 0.5)));
        for (var index = 0; index < impostor; index++)
            rows.Add(Observation(now, IdentityObservationReviewStatus.Reviewed,
                IdentityReviewDecision.Rejected, null,
                (GenuinePerson, 0.5), (OtherPerson, 0.4)));
        for (var index = 0; index < inconclusive; index++)
            rows.Add(Observation(now, IdentityObservationReviewStatus.Reviewed,
                IdentityReviewDecision.Inconclusive, null, (GenuinePerson, 0.3)));
        for (var index = 0; index < pending; index++)
            rows.Add(Observation(now, IdentityObservationReviewStatus.Pending, null, null,
                (GenuinePerson, 0.4)));

        database.IdentityObservations.AddRange(rows);
        await database.SaveChangesAsync();
    }

    private static IdentityObservation Observation(DateTimeOffset now,
        IdentityObservationReviewStatus reviewStatus, IdentityReviewDecision? decision, string? resolvedPerson,
        params (string Person, double Score)[] candidates)
    {
        var id = Guid.NewGuid();
        var observation = new IdentityObservation
        {
            Id = id,
            PublicId = "OBV" + Guid.NewGuid().ToString("N")[..12],
            ObservationHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            SchemaVersion = FaceObservationV1.CurrentSchemaVersion,
            ModelId = "test-face-model",
            ModelVersion = "contract-v1",
            EmbeddingSchemaVersion = FaceObservationV1.CurrentEmbeddingSchemaVersion,
            Dimensions = 3,
            CameraPublicId = "CAM000000000001",
            ClipPublicId = null,
            ObservedAtUtc = now,
            IngestedAtUtc = now,
            MatchStatus = (int)IdentityMatchStatus.Suggested,
            BestPersonPublicId = candidates.Length == 0 ? null : candidates[0].Person,
            BestScore = candidates.Length == 0 ? null : candidates[0].Score,
            CandidateMargin = candidates.Length < 2 ? null : candidates[0].Score - candidates[1].Score,
            EvaluatedEnrollments = candidates.Length,
            SkippedEnrollments = 0,
            PolicyVersion = IdentityMatchPolicy.BaselineVersion,
            PolicyThreshold = IdentityMatchPolicy.BaselineThreshold,
            PolicyMargin = IdentityMatchPolicy.BaselineMargin,
            ReviewStatus = (int)reviewStatus,
            ResolvedPersonPublicId = resolvedPerson,
            Version = 1
        };
        observation.Candidates = candidates.Select((candidate, index) => new CandidateRow
        {
            Id = Guid.NewGuid(),
            ObservationId = id,
            PersonPublicId = candidate.Person,
            Score = candidate.Score,
            Rank = index + 1
        }).ToList();
        if (decision is { } value)
        {
            observation.Reviews =
            [
                new IdentityObservationReview
                {
                    Id = Guid.NewGuid(),
                    PublicId = "RVW" + Guid.NewGuid().ToString("N")[..12],
                    ObservationId = id,
                    ReviewerUserId = Guid.NewGuid(),
                    ReviewerPublicId = "USR000000000001",
                    Decision = (int)value,
                    SelectedPersonPublicId = resolvedPerson,
                    CreatedAtUtc = now
                }
            ];
        }

        return observation;
    }

    private static string Escape(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O"));

    private static async Task LoginAsync(HttpClient client, string username, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username, password });
        Assert.True(response.IsSuccessStatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
    }

    private sealed class EmptyIdentityEmbeddingSource : IIdentityEmbeddingSource
    {
        public Task<IdentityEmbeddingSnapshot> LoadAsync(FaceObservationV1 observation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new IdentityEmbeddingSnapshot([], 0));

        public Task<IdentityEnrollmentEmbedding?> LoadEnrollmentAsync(string enrollmentPublicId,
            CancellationToken cancellationToken = default) => Task.FromResult<IdentityEnrollmentEmbedding?>(null);
    }
}
