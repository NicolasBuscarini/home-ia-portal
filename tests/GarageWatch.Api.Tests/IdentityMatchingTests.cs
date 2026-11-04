using System.Security.Cryptography;
using GarageWatch.Application.Biometrics;
using GarageWatch.Application.Identity;
using GarageWatch.Infrastructure.Biometrics;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class IdentityMatchingTests
{
    private const string TestKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string ModelId = "test-face-model";
    private const string ModelVersion = "contract-v1";

    [Fact]
    public void Matcher_suggests_the_best_person_when_threshold_and_margin_pass()
    {
        var result = new CosineIdentityMatcher().Match(Observation([0.9f, 0.1f, 0]),
        [
            Candidate("PER000000000101", 1, 0, 0),
            Candidate("PER000000000102", 0, 1, 0)
        ]);

        Assert.Equal(IdentityMatchStatus.Suggested, result.Status);
        Assert.Equal("PER000000000101", result.Best?.PersonPublicId);
        Assert.True(result.Margin > IdentityMatchPolicy.Baseline.Margin);
        Assert.Equal(2, result.EvaluatedEnrollments);
    }

    [Fact]
    public void Matcher_marks_ambiguous_when_the_top_two_scores_are_close()
    {
        var result = new CosineIdentityMatcher().Match(Observation([1f, 0, 0]),
        [
            Candidate("PER000000000101", 1, 0, 0),
            Candidate("PER000000000102", 0.999f, 0.001f, 0)
        ]);

        Assert.Equal(IdentityMatchStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Matcher_returns_unknown_below_threshold_and_no_enrollment_without_candidates()
    {
        var matcher = new CosineIdentityMatcher();
        var unknown = matcher.Match(Observation([1f, 0, 0]), [Candidate("PER000000000101", 0, 1, 0)]);
        Assert.Equal(IdentityMatchStatus.Unknown, unknown.Status);
        Assert.Null(unknown.Best);

        var none = matcher.Match(Observation([1f, 0, 0]), []);
        Assert.Equal(IdentityMatchStatus.NoEnrollment, none.Status);
        Assert.Empty(none.Candidates);
    }

    [Fact]
    public void Matcher_skips_embeddings_from_other_models_and_aggregates_per_person()
    {
        var matcher = new CosineIdentityMatcher();
        var skipped = matcher.Match(Observation([1f, 0, 0]),
            [Candidate("PER000000000101", 1, 0, 0) with { ModelVersion = "other" }]);
        Assert.Equal(IdentityMatchStatus.NoEnrollment, skipped.Status);
        Assert.Equal(1, skipped.SkippedEnrollments);

        var aggregated = matcher.Match(Observation([1f, 0, 0]),
        [
            Candidate("PER000000000101", 1, 0, 0),
            Candidate("PER000000000101", 0, 1, 0),
            Candidate("PER000000000102", 0, 0, 1)
        ]);
        Assert.Equal(IdentityMatchStatus.Suggested, aggregated.Status);
        Assert.Equal("PER000000000101", aggregated.Best?.PersonPublicId);
        Assert.Equal(2, aggregated.Candidates.Count);
    }

    [Fact]
    public void Protector_roundtrips_the_embedding_and_rejects_unknown_key_versions()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AesGcmBiometricDataProtector.KeySetting] = TestKey,
            [AesGcmBiometricDataProtector.KeyVersionSetting] = "test-1"
        }).Build();
        var protector = new AesGcmBiometricDataProtector(configuration,
            NullLogger<AesGcmBiometricDataProtector>.Instance);
        var protectedData = protector.Protect(BiometricEmbeddingCodec.Serialize([0.1f, 0.2f, 0.3f]));

        var plaintext = protector.Unprotect(protectedData);
        var decoded = BiometricEmbeddingCodec.Deserialize(plaintext, 3);
        Assert.NotNull(decoded);
        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f }, decoded);

        Assert.Throws<CryptographicException>(() => protector.Unprotect(
            protectedData with { KeyVersion = "old-key" }));
    }

    [Fact]
    public async Task Local_source_suggests_a_linked_person_for_a_matching_embedding()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory, "PER000000000101", "BIO000000000101", [0.1f, 0.2f, 0.3f]);

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IdentityMatchService>()
            .MatchAsync(Observation([0.1f, 0.2f, 0.3f]));

        Assert.Equal(IdentityMatchStatus.Suggested, result.Status);
        Assert.Equal("PER000000000101", result.Best?.PersonPublicId);
        Assert.True(result.Best!.Score > 0.999);
        Assert.Equal(1, result.EvaluatedEnrollments);
    }

    [Fact]
    public async Task Local_source_marks_ambiguous_and_unknown_with_real_encrypted_rows()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory, "PER000000000101", "BIO000000000101", [1f, 0, 0]);
        await SeedAsync(factory, "PER000000000102", "BIO000000000102", [0.999f, 0.001f, 0]);

        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IdentityMatchService>();
        var ambiguous = await service.MatchAsync(Observation([1f, 0, 0]));
        Assert.Equal(IdentityMatchStatus.Ambiguous, ambiguous.Status);
        Assert.Equal(2, ambiguous.Candidates.Count);

        var unknown = await service.MatchAsync(Observation([0f, 1f, 0]));
        Assert.Equal(IdentityMatchStatus.Unknown, unknown.Status);
    }

    [Fact]
    public async Task Local_source_skips_unreadable_rows_and_returns_no_enrollment_when_none_remain()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await SeedAsync(factory, "PER000000000101", "BIO000000000101", [0.1f, 0.2f, 0.3f],
            keyVersion: "old-key");

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IdentityMatchService>()
            .MatchAsync(Observation([0.1f, 0.2f, 0.3f]));

        Assert.Equal(IdentityMatchStatus.NoEnrollment, result.Status);
        Assert.Equal(1, result.SkippedEnrollments);
    }

    [Fact]
    public async Task Local_source_returns_no_enrollment_without_decryptable_candidates()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IdentityMatchService>()
            .MatchAsync(Observation([0.1f, 0.2f, 0.3f]));

        Assert.Equal(IdentityMatchStatus.NoEnrollment, result.Status);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Remote_biometric_topology_fails_closed_without_a_ciphertext_read_contract()
    {
        await using var factory = CreateFactory("remote");
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        await Assert.ThrowsAsync<IdentityMatchingUnavailableException>(() =>
            scope.ServiceProvider.GetRequiredService<IdentityMatchService>()
                .MatchAsync(Observation([0.1f, 0.2f, 0.3f])));
    }

    private static WebApplicationFactory<Program> CreateFactory(string monitoringBiometricsSource = "local") =>
        new ApiFactory(monitoringBiometricsSource: monitoringBiometricsSource).WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [AesGcmBiometricDataProtector.KeySetting] = TestKey,
                    [AesGcmBiometricDataProtector.KeyVersionSetting] = "test-1"
                }));
        });

    private static async Task SeedAsync(WebApplicationFactory<Program> factory, string personPublicId,
        string enrollmentPublicId, float[] values, string keyVersion = "test-1")
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var protector = scope.ServiceProvider.GetRequiredService<IBiometricDataProtector>();
        var user = new ApplicationUser
        {
            UserName = $"match-{Guid.NewGuid():N}",
            Email = $"match-{Guid.NewGuid():N}@example.test",
            EmailConfirmed = true
        };
        var created = await users.CreateAsync(user, "Matching-Password-123!");
        if (!created.Succeeded)
            throw new InvalidOperationException(string.Join(";", created.Errors.Select(error => error.Code)));
        var now = DateTimeOffset.UtcNow;
        var protectedData = protector.Protect(BiometricEmbeddingCodec.Serialize(values));

        database.KnownPeople.Add(new KnownPerson
        {
            Id = Guid.NewGuid(),
            PublicId = personPublicId,
            DisplayName = "Pessoa autorizada",
            Purpose = "Teste de matching",
            AuthorizationRecordedAtUtc = now,
            CreatedAtUtc = now,
            Version = 1
        });
        database.BiometricEnrollments.Add(new BiometricEnrollment
        {
            Id = Guid.NewGuid(),
            PublicId = enrollmentPublicId,
            UserId = user.Id,
            Status = BiometricEnrollment.ActiveStatus,
            EnrolledAtUtc = now,
            ExpiresAtUtc = now.AddDays(365),
            EmbeddingCiphertext = protectedData.Ciphertext,
            EmbeddingNonce = protectedData.Nonce,
            EmbeddingTag = protectedData.Tag,
            KeyVersion = keyVersion,
            Dimensions = values.Length,
            ModelId = ModelId,
            ModelVersion = ModelVersion,
            SchemaVersion = FaceObservationV1.CurrentEmbeddingSchemaVersion,
            AdapterId = "test",
            ConsentVersion = BiometricPolicy.ConsentVersion,
            ConsentAcceptedAtUtc = now,
            CreatedByUserId = user.Id,
            CreatedAtUtc = now
        });
        database.BiometricPersonLinks.Add(new BiometricPersonLink
        {
            Id = Guid.NewGuid(),
            EnrollmentPublicId = enrollmentPublicId,
            KnownPersonPublicId = personPublicId,
            LinkedByUserId = user.Id,
            LinkedAtUtc = now
        });
        await database.SaveChangesAsync();
    }

    private static FaceObservationV1 Observation(float[] values) => new(
        FaceObservationV1.CurrentSchemaVersion, ModelId, ModelVersion,
        FaceObservationV1.CurrentEmbeddingSchemaVersion, values.Length, values,
        ObservedAtUtc: DateTimeOffset.UtcNow);

    private static IdentityEmbeddingCandidate Candidate(string personPublicId, params float[] values) => new(
        personPublicId, "BIO000000000101", ModelId, ModelVersion,
        FaceObservationV1.CurrentEmbeddingSchemaVersion, values.Length, values);
}
