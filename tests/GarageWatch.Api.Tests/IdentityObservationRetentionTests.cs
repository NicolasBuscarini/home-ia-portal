using GarageWatch.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class IdentityObservationRetentionTests
{
    [Fact]
    public async Task Expires_old_observations_with_candidates_and_reviews_and_keeps_recent_ones()
    {
        await using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var now = DateTimeOffset.UtcNow;
        var old = Observation("OBV000000000101", "RVW000000000101", now.AddDays(-31));
        var recent = Observation("OBV000000000102", "RVW000000000102", now.AddDays(-1));
        database.IdentityObservations.AddRange(old, recent);
        await database.SaveChangesAsync();

        var service = new IdentityObservationRetentionService(database,
            new IdentityObservationRetentionOptions(true, 30, 60, 100), TimeProvider.System);
        var deleted = await service.RunOnceAsync();

        Assert.Equal(1, deleted);
        Assert.Null(await database.IdentityObservations
            .SingleOrDefaultAsync(item => item.PublicId == "OBV000000000101"));
        Assert.NotNull(await database.IdentityObservations
            .SingleOrDefaultAsync(item => item.PublicId == "OBV000000000102"));
        Assert.False(await database.IdentityObservationCandidates
            .AnyAsync(item => item.ObservationId == old.Id));
        Assert.False(await database.IdentityObservationReviews
            .AnyAsync(item => item.ObservationId == old.Id));
        Assert.True(await database.IdentityObservationReviews
            .AnyAsync(item => item.ObservationId == recent.Id));
    }

    [Fact]
    public async Task Disabled_retention_keeps_every_observation()
    {
        await using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var old = Observation("OBV000000000201", "RVW000000000201", DateTimeOffset.UtcNow.AddDays(-400));
        database.IdentityObservations.Add(old);
        await database.SaveChangesAsync();

        var service = new IdentityObservationRetentionService(database,
            new IdentityObservationRetentionOptions(false, 30, 60, 100), TimeProvider.System);
        Assert.Equal(0, await service.RunOnceAsync());
        Assert.NotNull(await database.IdentityObservations
            .SingleOrDefaultAsync(item => item.PublicId == "OBV000000000201"));
    }

    private static IdentityObservation Observation(string publicId, string reviewPublicId,
        DateTimeOffset ingestedAtUtc) => new()
        {
            Id = Guid.NewGuid(),
            PublicId = publicId,
            ObservationHash = new string(publicId.EndsWith("101", StringComparison.Ordinal) ? 'a' : 'b', 64),
            SchemaVersion = "face-observation.v1",
            ModelId = "test-face-model",
            ModelVersion = "contract-v1",
            EmbeddingSchemaVersion = "face-embedding.v1",
            Dimensions = 128,
            ObservedAtUtc = ingestedAtUtc,
            IngestedAtUtc = ingestedAtUtc,
            MatchStatus = (int)GarageWatch.Application.Identity.IdentityMatchStatus.Suggested,
            BestPersonPublicId = "PER000000000001",
            BestScore = 0.91,
            PolicyVersion = "identity-match.baseline.v1",
            PolicyThreshold = 0.363,
            PolicyMargin = 0.05,
            ReviewStatus = (int)GarageWatch.Application.Identity.IdentityObservationReviewStatus.Reviewed,
            ResolvedPersonPublicId = "PER000000000001",
            LatestReviewPublicId = reviewPublicId,
            Version = 2,
            Candidates =
        [
            new IdentityObservationCandidate
            {
                Id = Guid.NewGuid(),
                PersonPublicId = "PER000000000001",
                Score = 0.91,
                Rank = 1
            }
        ],
            Reviews =
        [
            new IdentityObservationReview
            {
                Id = Guid.NewGuid(),
                PublicId = reviewPublicId,
                ReviewerUserId = Guid.NewGuid(),
                ReviewerPublicId = "USR000000000001",
                Decision = (int)GarageWatch.Application.Identity.IdentityReviewDecision.Confirmed,
                SelectedPersonPublicId = "PER000000000001",
                CreatedAtUtc = ingestedAtUtc
            }
        ]
        };
}
