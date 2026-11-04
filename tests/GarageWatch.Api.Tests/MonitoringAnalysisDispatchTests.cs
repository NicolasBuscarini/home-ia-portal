using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class MonitoringAnalysisDispatchTests
{
    [Fact]
    public async Task Remote_policy_source_proxies_versioned_policy_and_keeps_portal_authorization()
    {
        var remote = new FakeAnalysisClient
        {
            Policy = new MonitoringAnalysisPolicy("garage-01", "always", 3,
                DateTimeOffset.Parse("2026-11-02T12:00:00+00:00"))
        };
        await using var factory = new ApiFactory(monitoringAnalysisSource: "remote",
            monitoringAnalysisClient: remote);
        var cameraId = factory.Services.GetRequiredService<CaptureOptions>().CameraId;
        var cameraPublicId = await GetCameraPublicIdAsync(factory);
        using var admin = await LoginAsync(factory);
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var read = await admin.GetAsync(
            $"/api/v1/cameras/{cameraPublicId}/clip-analysis-dispatch-policy");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var body = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("always", body.GetProperty("policy").GetString());
        Assert.Equal(3, body.GetProperty("version").GetInt64());
        Assert.True(body.GetProperty("canManage").GetBoolean());

        using var update = await admin.PutAsJsonAsync(
            $"/api/v1/cameras/{cameraPublicId}/clip-analysis-dispatch-policy",
            new { expectedVersion = 3, policy = "large_motion" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("large_motion", updated.GetProperty("policy").GetString());
        Assert.Equal(4, updated.GetProperty("version").GetInt64());
        Assert.Equal(cameraId, remote.LastCameraId);
        Assert.Equal(3, remote.LastExpectedVersion);
        Assert.Equal("large_motion", remote.LastPolicy);
        Assert.True(Guid.TryParse(remote.LastActorId, out _));
    }

    [Fact]
    public async Task Remote_manual_request_uses_the_service_queue_and_claim_maps_back()
    {
        var remote = new FakeAnalysisClient
        {
            Policy = new MonitoringAnalysisPolicy("garage-01", "always", 1, DateTimeOffset.UtcNow),
            Enqueued = new MonitoringAnalysisJob("JOB000000000001", "CLP000000000001", "analysis-v1",
                "analysis_pending", false),
            Claimed = new MonitoringAnalysisJob("JOB000000000001", "CLP000000000001", "analysis-v1",
                "processing", false)
        };
        await using var factory = new ApiFactory(monitoringAnalysisSource: "remote",
            monitoringAnalysisClient: remote);
        var cameraId = factory.Services.GetRequiredService<CaptureOptions>().CameraId;
        var clip = await PersistReadyClipAsync(factory, cameraId);
        using var admin = await LoginAsync(factory);
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");
        var idempotencyKey = Guid.NewGuid();

        using var request = await admin.PostAsJsonAsync($"/api/v1/clips/{clip.PublicId}/analysis-requests",
            new { idempotencyKey });
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        var job = await request.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("JOB000000000001", job.GetProperty("publicId").GetString());
        Assert.Equal(clip.ClipId, remote.LastEnqueue?.ClipId);
        Assert.Equal(clip.PublicId, remote.LastEnqueue?.ClipPublicId);
        Assert.Equal("manual", remote.LastEnqueue?.RequestKind);
        Assert.Equal(idempotencyKey, remote.LastEnqueue?.IdempotencyKey);

        using (var scope = factory.Services.CreateScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<IamDbContext>().ClipAnalysisJobs
                .ToArrayAsync());

    }

    [Fact]
    public async Task Remote_unavailability_is_explicit_and_does_not_silently_use_local_state()
    {
        var remote = new FakeAnalysisClient { Unavailable = true };
        await using var factory = new ApiFactory(monitoringAnalysisSource: "remote",
            monitoringAnalysisClient: remote);
        var cameraPublicId = await GetCameraPublicIdAsync(factory);
        using var admin = await LoginAsync(factory);
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var response = await admin.GetAsync(
            $"/api/v1/cameras/{cameraPublicId}/clip-analysis-dispatch-policy");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("analysis_dispatch_unavailable",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    private static async Task<ClipManifestV1> PersistReadyClipAsync(ApiFactory factory, string cameraId)
    {
        var capture = factory.Services.GetRequiredService<CaptureOptions>();
        var clipId = Guid.NewGuid().ToString("N");
        var mediaDirectory = Path.Combine(capture.SpoolDirectory, "clips", "media");
        var manifestDirectory = Path.Combine(capture.SpoolDirectory, "clips", "manifests");
        Directory.CreateDirectory(mediaDirectory);
        Directory.CreateDirectory(manifestDirectory);
        var mediaName = $"{clipId}.ts";
        var media = Path.Combine(mediaDirectory, mediaName);
        await File.WriteAllBytesAsync(media, [0x47, 0x01, 0x02]);
        await using var stream = File.OpenRead(media);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var clip = new ClipManifestV1("1.0", clipId, cameraId, now, now, now, now, "ready", "trigger-v1",
            "visual_change:test", [], mediaName, hash, null, null, 1, now,
            PublicId: new PublicIdGenerator().Generate(PublicIdPrefixes.ClipPrefix));
        await File.WriteAllTextAsync(Path.Combine(manifestDirectory, $"{clipId}.json"),
            JsonSerializer.Serialize(clip, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return clip;
    }

    private static async Task<string> GetCameraPublicIdAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var internalId = factory.Services.GetRequiredService<CaptureOptions>().CameraId;
        return (await scope.ServiceProvider.GetRequiredService<IamDbContext>().CameraCatalog
            .Where(item => item.CameraId == internalId).Select(item => item.PublicId).SingleAsync())!;
    }

    private static async Task<HttpClient> LoginAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "Contract-Password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        return client;
    }

    private sealed class FakeAnalysisClient : IMonitoringAnalysisClient
    {
        public MonitoringAnalysisPolicy? Policy { get; init; }
        public MonitoringAnalysisJob? Enqueued { get; init; }
        public MonitoringAnalysisJob? Claimed { get; init; }
        public bool Unavailable { get; init; }
        public string? LastCameraId { get; private set; }
        public long LastExpectedVersion { get; private set; }
        public string? LastPolicy { get; private set; }
        public string? LastActorId { get; private set; }
        public MonitoringAnalysisEnqueue? LastEnqueue { get; private set; }

        public Task<MonitoringAnalysisPolicy?> GetPolicyAsync(string cameraId,
            CancellationToken cancellationToken = default)
        {
            LastCameraId = cameraId;
            if (Unavailable) throw new MonitoringAnalysisUnavailableException();
            return Task.FromResult(Policy);
        }

        public Task<MonitoringAnalysisPolicy?> UpdatePolicyAsync(string cameraId, string contextId,
            long expectedVersion, string policy, string actorId, CancellationToken cancellationToken = default)
        {
            LastCameraId = cameraId;
            LastExpectedVersion = expectedVersion;
            LastPolicy = policy;
            LastActorId = actorId;
            if (Unavailable) throw new MonitoringAnalysisUnavailableException();
            return Task.FromResult<MonitoringAnalysisPolicy?>(new MonitoringAnalysisPolicy(cameraId, policy,
                expectedVersion + 1, DateTimeOffset.UtcNow));
        }

        public Task<MonitoringAnalysisJob?> EnqueueAsync(MonitoringAnalysisEnqueue request,
            CancellationToken cancellationToken = default)
        {
            LastEnqueue = request;
            if (Unavailable) throw new MonitoringAnalysisUnavailableException();
            return Task.FromResult(Enqueued);
        }

        public Task<MonitoringAnalysisJob?> ClaimAsync(CancellationToken cancellationToken = default)
        {
            if (Unavailable) throw new MonitoringAnalysisUnavailableException();
            return Task.FromResult(Claimed);
        }

        public Task<IReadOnlyList<MonitoringAnalysisJobStatus>?> LatestForClipsAsync(
            IReadOnlyCollection<string> clipPublicIds, CancellationToken cancellationToken = default)
        {
            if (Unavailable) throw new MonitoringAnalysisUnavailableException();
            return Task.FromResult<IReadOnlyList<MonitoringAnalysisJobStatus>?>([]);
        }
    }
}
