using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GarageWatch.Application.Analysis;
using GarageWatch.Application.Biometrics;
using GarageWatch.Application.Identity;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class IdentityTriageApiTests
{
    private const string Password = "Identity-Triage-Password-123!";
    private const string ServiceSecret = "test-monitoring-service-secret-0123456789abcdef";
    private static readonly string TestKey = Convert.ToBase64String(
        Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());

    [Fact]
    public async Task Monitoring_ingests_an_idempotent_observation_and_admin_reviews_it()
    {
        await using var factory = ConfiguredFactory();
        using var admin = factory.CreateClient();
        await LoginAsync(admin, "contract-user", "Contract-Password-123!");
        var ownerPublicId = await RegisterUserAsync(factory, "triage-owner");
        var enrollmentId = await EnrollAsync(admin, ownerPublicId);
        var personId = await CreatePersonAsync(admin, "Pessoa Autorizada");
        await LinkAsync(admin, enrollmentId, personId);

        using var service = factory.CreateClient();
        service.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Mint(IdentityObservationScopes.Write));

        var observedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        var observation = new
        {
            schemaVersion = FaceObservationV1.CurrentSchemaVersion,
            modelId = "test-face-model",
            modelVersion = "contract-v1",
            embeddingSchemaVersion = FaceObservationV1.CurrentEmbeddingSchemaVersion,
            dimensions = 3,
            values = new[] { 0.1f, 0.2f, 0.3f },
            cameraPublicId = "CAM000000000001",
            clipPublicId = "CLP000000000001",
            observedAtUtc = observedAt
        };

        using var created = await service.PostAsJsonAsync("/api/v1/monitoring/identity-observations", observation);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var text = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"values\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("embedding", text, StringComparison.OrdinalIgnoreCase);
        using var createdBody = JsonDocument.Parse(text);
        var summary = createdBody.RootElement.GetProperty("summary");
        var observationId = summary.GetProperty("publicId").GetString()!;
        Assert.StartsWith("OBV", observationId, StringComparison.Ordinal);
        Assert.Equal("suggested", summary.GetProperty("matchStatus").GetString());
        Assert.Equal(personId, summary.GetProperty("bestPersonPublicId").GetString());
        Assert.Equal("pending", summary.GetProperty("reviewStatus").GetString());
        Assert.Single(createdBody.RootElement.GetProperty("candidates").EnumerateArray());

        using var duplicate = await service.PostAsJsonAsync("/api/v1/monitoring/identity-observations",
            observation);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        using var duplicateBody = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(observationId,
            duplicateBody.RootElement.GetProperty("summary").GetProperty("publicId").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var stored = await database.IdentityObservations.Include(item => item.Candidates)
                .SingleAsync(item => item.PublicId == observationId);
            Assert.Equal(64, stored.ObservationHash.Length);
            Assert.Single(stored.Candidates);
            Assert.Equal(personId, stored.BestPersonPublicId);
            Assert.Null(stored.ResolvedPersonPublicId);
            Assert.Equal(1, await database.IdentityObservations.CountAsync());
        }

        using var pending = await admin.GetAsync(
            "/api/v1/admin/identity-observations?reviewStatus=pending&cameraPublicId=CAM000000000001");
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        using var pendingBody = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        Assert.Equal(1, pendingBody.RootElement.GetProperty("total").GetInt32());

        using (var invalidReview = await admin.PostAsJsonAsync(
            $"/api/v1/admin/identity-observations/{observationId}/reviews",
            new { decision = "confirmed", personPublicId = "PER999999999999" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidReview.StatusCode);
        }

        using var reviewed = await admin.PostAsJsonAsync(
            $"/api/v1/admin/identity-observations/{observationId}/reviews",
            new { decision = "confirmed", personPublicId = personId, note = "Confirmado no teste sintético" });
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        using var reviewBody = JsonDocument.Parse(await reviewed.Content.ReadAsStringAsync());
        Assert.Equal("confirmed", reviewBody.RootElement.GetProperty("decision").GetString());

        using var detail = await admin.GetAsync($"/api/v1/admin/identity-observations/{observationId}");
        using var detailBody = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var reviewedSummary = detailBody.RootElement.GetProperty("summary");
        Assert.Equal("reviewed", reviewedSummary.GetProperty("reviewStatus").GetString());
        Assert.Equal(personId, reviewedSummary.GetProperty("resolvedPersonPublicId").GetString());
        Assert.Equal("suggested", reviewedSummary.GetProperty("matchStatus").GetString());
        Assert.Equal(personId, reviewedSummary.GetProperty("bestPersonPublicId").GetString());
        Assert.Equal(1, detailBody.RootElement.GetProperty("reviews").GetArrayLength());

        using (var secondReview = await admin.PostAsJsonAsync(
            $"/api/v1/admin/identity-observations/{observationId}/reviews",
            new { decision = "rejected" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, secondReview.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var eventRow = await database.PlatformEvents
                .SingleAsync(entry => entry.EventType == "identity.observation.reviewed");
            Assert.Equal(observationId, eventRow.ResourceId);
            Assert.Contains(personId, eventRow.MetadataJson, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Ingest_requires_the_monitoring_identity_scope_and_review_requires_privilege()
    {
        await using var factory = ConfiguredFactory();
        using var service = factory.CreateClient();
        var body = ObservationBody();

        service.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Mint("events.write"));
        using (var wrongScope = await service.PostAsJsonAsync("/api/v1/monitoring/identity-observations", body))
            Assert.Equal(HttpStatusCode.Unauthorized, wrongScope.StatusCode);

        service.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Mint(IdentityObservationScopes.Write, subject: "activitywatch"));
        using (var wrongSource = await service.PostAsJsonAsync("/api/v1/monitoring/identity-observations", body))
            Assert.Equal(HttpStatusCode.Unauthorized, wrongSource.StatusCode);

        service.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Mint(IdentityObservationScopes.Write));
        using (var invalid = await service.PostAsJsonAsync("/api/v1/monitoring/identity-observations",
                   new
                   {
                       schemaVersion = FaceObservationV1.CurrentSchemaVersion,
                       modelId = "model",
                       modelVersion = "v1",
                       embeddingSchemaVersion = FaceObservationV1.CurrentEmbeddingSchemaVersion,
                       dimensions = 3,
                       values = new[] { 0.1f }
                   }))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var created = await service.PostAsJsonAsync("/api/v1/monitoring/identity-observations", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var observationId = createdBody.RootElement.GetProperty("summary").GetProperty("publicId").GetString();

        using var user = factory.CreateClient();
        await RegisterAsync(user, "triage-user");
        await LoginAsync(user, "triage-user");
        using (var deniedList = await user.GetAsync("/api/v1/admin/identity-observations"))
            Assert.Equal(HttpStatusCode.Forbidden, deniedList.StatusCode);
        using (var deniedReview = await user.PostAsJsonAsync(
                   $"/api/v1/admin/identity-observations/{observationId}/reviews",
                   new { decision = "inconclusive" }))
            Assert.Equal(HttpStatusCode.Forbidden, deniedReview.StatusCode);

        using var admin = factory.CreateClient();
        await LoginAsync(admin, "contract-user", "Contract-Password-123!");
        using var notPending = await admin.PostAsJsonAsync(
            $"/api/v1/admin/identity-observations/{observationId}/reviews",
            new { decision = "inconclusive" });
        Assert.Equal(HttpStatusCode.Conflict, notPending.StatusCode);
    }

    [Fact]
    public async Task Admin_can_reject_mark_inconclusive_and_associate_another_roster_person()
    {
        await using var factory = ConfiguredFactory();
        using var admin = factory.CreateClient();
        await LoginAsync(admin, "contract-user", "Contract-Password-123!");
        var ownerPublicId = await RegisterUserAsync(factory, "triage-decisions-owner");
        var enrollmentId = await EnrollAsync(admin, ownerPublicId);
        var suggestedPerson = await CreatePersonAsync(admin, "Pessoa Sugerida");
        var associatedPerson = await CreatePersonAsync(admin, "Pessoa Associada");
        var deletedPerson = await CreatePersonAsync(admin, "Pessoa Removida");
        await LinkAsync(admin, enrollmentId, suggestedPerson);
        using (var removed = await admin.DeleteAsync($"/api/v1/admin/known-people/{deletedPerson}"))
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        using var service = factory.CreateClient();
        service.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Mint(IdentityObservationScopes.Write));

        var rejectedId = await IngestAsync(service, "CLP000000000101");
        using (var rejected = await admin.PostAsJsonAsync(
                   $"/api/v1/admin/identity-observations/{rejectedId}/reviews",
                   new { decision = "rejected" }))
        {
            Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
            using var body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("selectedPersonPublicId").ValueKind);
        }

        var inconclusiveId = await IngestAsync(service, "CLP000000000102");
        using (var inconclusive = await admin.PostAsJsonAsync(
                   $"/api/v1/admin/identity-observations/{inconclusiveId}/reviews",
                   new { decision = "inconclusive" }))
            Assert.Equal(HttpStatusCode.OK, inconclusive.StatusCode);

        var associatedId = await IngestAsync(service, "CLP000000000103");
        using (var associated = await admin.PostAsJsonAsync(
                   $"/api/v1/admin/identity-observations/{associatedId}/reviews",
                   new { decision = "associated", personPublicId = associatedPerson }))
            Assert.Equal(HttpStatusCode.OK, associated.StatusCode);
        using (var detail = await admin.GetAsync($"/api/v1/admin/identity-observations/{associatedId}"))
        {
            using var body = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            Assert.Equal(associatedPerson,
                body.RootElement.GetProperty("summary").GetProperty("resolvedPersonPublicId").GetString());
            Assert.Equal(suggestedPerson,
                body.RootElement.GetProperty("summary").GetProperty("bestPersonPublicId").GetString());
        }

        var deletedTargetId = await IngestAsync(service, "CLP000000000104");
        using (var deletedTarget = await admin.PostAsJsonAsync(
                   $"/api/v1/admin/identity-observations/{deletedTargetId}/reviews",
                   new { decision = "associated", personPublicId = deletedPerson }))
            Assert.Equal(HttpStatusCode.BadRequest, deletedTarget.StatusCode);
    }

    private static async Task<string> IngestAsync(HttpClient service, string clipPublicId)
    {
        using var response = await service.PostAsJsonAsync("/api/v1/monitoring/identity-observations",
            new
            {
                schemaVersion = FaceObservationV1.CurrentSchemaVersion,
                modelId = "test-face-model",
                modelVersion = "contract-v1",
                embeddingSchemaVersion = FaceObservationV1.CurrentEmbeddingSchemaVersion,
                dimensions = 3,
                values = new[] { 0.1f, 0.2f, 0.3f },
                cameraPublicId = "CAM000000000003",
                clipPublicId,
                observedAtUtc = DateTimeOffset.UtcNow
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("summary").GetProperty("publicId").GetString()!;
    }

    private static WebApplicationFactory<Program> ConfiguredFactory() =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BIOMETRIC_ENCRYPTION_KEY"] = TestKey,
                    ["BIOMETRIC_ENCRYPTION_KEY_VERSION"] = "test-1",
                    ["MONITORING_BIOMETRICS_SOURCE"] = "local"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFaceEnrollmentExtractor>();
                services.AddSingleton<IFaceEnrollmentExtractor>(new TestEnrollmentExtractor());
                services.RemoveAll<IBiometricEnrollmentGateway>();
                services.AddScoped<IBiometricEnrollmentGateway, LocalBiometricEnrollmentGateway>();
                services.RemoveAll<IKnownPeopleGateway>();
                services.AddScoped<IKnownPeopleGateway>(provider =>
                    provider.GetRequiredService<KnownPersonService>());
            });
        });

    private static object ObservationBody() => new
    {
        schemaVersion = FaceObservationV1.CurrentSchemaVersion,
        modelId = "test-face-model",
        modelVersion = "contract-v1",
        embeddingSchemaVersion = FaceObservationV1.CurrentEmbeddingSchemaVersion,
        dimensions = 3,
        values = new[] { 0.1f, 0.2f, 0.3f },
        cameraPublicId = "CAM000000000002",
        clipPublicId = "CLP000000000002",
        observedAtUtc = DateTimeOffset.UtcNow
    };

    private static string Mint(string scope, string subject = "monitoring")
    {
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" })));
        var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iss = "monitoring",
            aud = "home-ia-portal",
            sub = subject,
            residence_id = "home",
            scope,
            iat = now.AddSeconds(-1).ToUnixTimeSeconds(),
            exp = now.AddMinutes(1).ToUnixTimeSeconds(),
            jti = Guid.NewGuid().ToString("N")
        })));
        var signature = Base64Url.EncodeToString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(ServiceSecret),
            Encoding.ASCII.GetBytes($"{header}.{payload}")));
        return $"{header}.{payload}.{signature}";
    }

    private static async Task<string> RegisterUserAsync(WebApplicationFactory<Program> factory, string username)
    {
        using var client = factory.CreateClient();
        await RegisterAsync(client, username);
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await users.FindByNameAsync(username))!.PublicId!;
    }

    private static async Task<string> EnrollAsync(HttpClient client, string userPublicId)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/biometrics/enrollments",
            new
            {
                userPublicId,
                mediaType = "image/jpeg",
                width = 160,
                height = 160,
                imageBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic-face-image")),
                consentVersion = BiometricPolicy.ConsentVersion
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("publicId").GetString()!;
    }

    private static async Task<string> CreatePersonAsync(HttpClient client, string displayName)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/admin/known-people",
            new { displayName, purpose = "Teste sintético de triagem", authorizationConfirmed = true });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("publicId").GetString()!;
    }

    private static async Task LinkAsync(HttpClient client, string enrollmentId, string personId)
    {
        using var response = await client.PutAsJsonAsync(
            $"/api/v1/admin/biometrics/enrollments/{enrollmentId}/person",
            new { personPublicId = personId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task RegisterAsync(HttpClient client, string username)
    {
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var registered = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = Password, confirmPassword = Password });
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
    }

    private static async Task LoginAsync(HttpClient client, string username, string? password = null)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username, password = password ?? Password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
    }

    private sealed class TestEnrollmentExtractor : IFaceEnrollmentExtractor
    {
        public string ModelId => "test-face-model";
        public string ModelVersion => "contract-v1";
        public string AdapterId => "test-face-adapter";

        public ValueTask<FaceEmbeddingV1> ExtractAsync(FaceCropV1 crop, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!crop.IsValid) throw new ArgumentException("Recorte inválido.", nameof(crop));
            return ValueTask.FromResult(new FaceEmbeddingV1(FaceEmbeddingV1.CurrentSchemaVersion,
                ModelId, ModelVersion, 3, [0.1f, 0.2f, 0.3f]));
        }
    }
}
