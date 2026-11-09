using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GarageWatch.Application.Identity;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class IdentityObservationFrameApiTests
{
    private const string ServiceSecret = "test-monitoring-service-secret-0123456789abcdef";
    private static readonly string TestKey = Convert.ToBase64String(
        Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());

    [Fact]
    public async Task Review_frames_resolve_through_the_observation_clip_and_the_access_is_audited()
    {
        var frames = new FakeFramesClient();
        await using var factory = ConfiguredFactory(frames);
        using var admin = factory.CreateClient();
        await LoginAsync(admin, "contract-user", "Contract-Password-123!");
        var observationId = await IngestAsync(factory, "CLP000000000001");

        using var manifest = await admin.GetAsync($"/api/v1/admin/identity-observations/{observationId}/frames");
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        var body = await manifest.Content.ReadFromJsonAsync<MonitoringClipFrameManifest>();
        Assert.Equal("CLP000000000001", body!.ClipPublicId);
        Assert.Equal(2, body.Frames.Count);
        Assert.Equal(5_000, body.Frames[1].OffsetMilliseconds);

        using var image = await admin.GetAsync($"/api/v1/admin/identity-observations/{observationId}/frames/1");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/jpeg", image.Content.Headers.ContentType?.MediaType);
        Assert.True(image.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("public", image.Headers.CacheControl?.ToString() ?? string.Empty);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0x11], await image.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, frames.ImageRequests);

        using var invalid = await admin.GetAsync($"/api/v1/admin/identity-observations/{observationId}/frames/12");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(1, frames.ImageRequests);

        using var scope = factory.Services.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<IamDbContext>().PlatformEvents
            .AsNoTracking()
            .Where(item => item.EventType == "identity.observation.frame.viewed")
            .ToArrayAsync();
        var recorded = Assert.Single(events);
        Assert.Equal("success", recorded.Outcome);
        Assert.Equal("identity_observation", recorded.ResourceType);
        Assert.Equal(observationId, recorded.ResourceId);
    }

    [Fact]
    public async Task Observation_without_clip_and_reviewer_without_biometric_management_are_refused()
    {
        var frames = new FakeFramesClient();
        await using var factory = ConfiguredFactory(frames);
        using var admin = factory.CreateClient();
        await LoginAsync(admin, "contract-user", "Contract-Password-123!");
        var observationId = await IngestAsync(factory, null);

        using var conflict = await admin.GetAsync($"/api/v1/admin/identity-observations/{observationId}/frames");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(0, frames.ManifestRequests);

        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var reviewerUser = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = "clip-less-reviewer",
                Email = "clip-less-reviewer@example.invalid",
                IsActive = true
            };
            var created = await userManager.CreateAsync(reviewerUser, "Clip-Less-Password-123!");
            Assert.True(created.Succeeded, string.Join(",", created.Errors.Select(error => error.Code)));
            Assert.True((await userManager.AddToRoleAsync(reviewerUser, "Viewer")).Succeeded);
        }
        using var reviewer = factory.CreateClient();
        await LoginAsync(reviewer, "clip-less-reviewer", "Clip-Less-Password-123!");

        using var denied = await reviewer.GetAsync($"/api/v1/admin/identity-observations/{observationId}/frames");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    private static WebApplicationFactory<Program> ConfiguredFactory(IMonitoringClipFramesClient frames) =>
        new ApiFactory(monitoringClipFramesClient: frames).WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BIOMETRIC_ENCRYPTION_KEY"] = TestKey,
                    ["BIOMETRIC_ENCRYPTION_KEY_VERSION"] = "test-1",
                    ["MONITORING_BIOMETRICS_SOURCE"] = "local"
                })));

    private static async Task<string> IngestAsync(WebApplicationFactory<Program> factory, string? clipPublicId)
    {
        using var service = factory.CreateClient();
        service.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Mint(IdentityObservationScopes.Write));
        using var response = await service.PostAsJsonAsync("/api/v1/monitoring/identity-observations", new
        {
            schemaVersion = FaceObservationV1.CurrentSchemaVersion,
            modelId = "test-face-model",
            modelVersion = "contract-v1",
            embeddingSchemaVersion = FaceObservationV1.CurrentEmbeddingSchemaVersion,
            dimensions = 3,
            values = new[] { 0.1f, 0.2f, 0.3f },
            cameraPublicId = "CAM000000000001",
            clipPublicId,
            observedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-5)
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("summary").GetProperty("publicId").GetString()!;
    }

    private static async Task LoginAsync(HttpClient client, string username, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
    }

    private static string Mint(string scope)
    {
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" })));
        var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iss = "monitoring",
            aud = "home-ia-portal",
            sub = "monitoring",
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

    private sealed class FakeFramesClient : IMonitoringClipFramesClient
    {
        public int ManifestRequests { get; private set; }
        public int ImageRequests { get; private set; }

        public Task<MonitoringClipFrameManifest?> GetManifestAsync(string clipPublicId,
            CancellationToken cancellationToken = default)
        {
            ManifestRequests++;
            return Task.FromResult<MonitoringClipFrameManifest?>(new MonitoringClipFrameManifest(clipPublicId,
                10_000, 2, [new MonitoringClipFrame(0, 1_666), new MonitoringClipFrame(1, 5_000)]));
        }

        public Task<MonitoringClipFrameImage?> GetFrameAsync(string clipPublicId, int index,
            CancellationToken cancellationToken = default)
        {
            ImageRequests++;
            return Task.FromResult<MonitoringClipFrameImage?>(
                new MonitoringClipFrameImage([0xFF, 0xD8, 0xFF, 0x11], "image/jpeg"));
        }
    }
}
