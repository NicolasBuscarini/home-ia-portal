using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class RemoteRetentionTests
{
    [Fact]
    public async Task Remote_policy_maps_public_camera_and_keeps_audit_in_the_portal()
    {
        var retention = new FakeRetentionClient();
        await using var factory = new ApiFactory(monitoringRetentionSource: "remote",
            monitoringRetentionClient: retention);
        var capture = factory.Services.GetRequiredService<CaptureOptions>();
        var cameraPublicId = await GetCameraPublicIdAsync(factory, capture.CameraId);
        retention.Policies[capture.CameraId] = new MonitoringRetentionPolicy(capture.CameraId, 30, 10, 2,
            DateTimeOffset.UtcNow);
        using var admin = await LoginAsync(factory);
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var current = await admin.GetAsync(
            $"/api/v1/admin/cameras/{cameraPublicId}/clip-retention");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var policy = await current.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(cameraPublicId, policy.GetProperty("cameraId").GetString());
        Assert.Equal(30, policy.GetProperty("retentionDays").GetInt32());
        Assert.Equal(10, policy.GetProperty("maximumStorageGigabytes").GetInt32());
        Assert.Equal(2, policy.GetProperty("version").GetInt64());
        Assert.Equal(capture.CameraId, retention.LastCameraId);

        using var saved = await admin.PutAsJsonAsync(
            $"/api/v1/admin/cameras/{cameraPublicId}/clip-retention",
            new { expectedVersion = 2, retentionDays = 20, maximumStorageGigabytes = 40 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var updated = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, updated.GetProperty("version").GetInt64());
        Assert.Equal(20, retention.Policies[capture.CameraId].RetentionDays);
        Assert.Equal(40, retention.Policies[capture.CameraId].MaximumStorageGigabytes);

        using var stale = await admin.PutAsJsonAsync(
            $"/api/v1/admin/cameras/{cameraPublicId}/clip-retention",
            new { expectedVersion = 0, retentionDays = 5, maximumStorageGigabytes = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("clip_retention_version_conflict",
            (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.Equal(["clip.retention.policy.updated"],
            await database.IamAuditEvents.Where(item => item.Action.StartsWith("clip."))
                .Select(item => item.Action).ToArrayAsync());
    }

    [Fact]
    public async Task Remote_preservation_round_trip_enriches_clips_and_audits_both_transitions()
    {
        await using var factory = new ApiFactory(monitoringRetentionSource: "remote",
            monitoringRetentionClient: new FakeRetentionClient());
        var capture = factory.Services.GetRequiredService<CaptureOptions>();
        var cameraPublicId = await GetCameraPublicIdAsync(factory, capture.CameraId);
        var clip = await WriteArchivedClipAsync(capture, capture.CameraId, DateTimeOffset.UtcNow);
        var retention = (FakeRetentionClient)factory.Services
            .GetRequiredService<IMonitoringRetentionClient>();
        retention.Preservations[clip.PublicId!] = new MonitoringPreservation(clip.PublicId!, true, 1);
        using var admin = await LoginAsync(factory);
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var list = await admin.GetAsync($"/api/v1/clips?cameraId={cameraPublicId}");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var page = await list.Content.ReadFromJsonAsync<JsonElement>();
        var item = page.GetProperty("items").EnumerateArray()
            .Single(entry => entry.GetProperty("publicId").GetString() == clip.PublicId);
        Assert.True(item.GetProperty("isPreserved").GetBoolean());
        Assert.Contains(clip.PublicId, retention.LastPreservationIds);

        using var released = await admin.PutAsJsonAsync(
            $"/api/v1/clips/{clip.PublicId}/preservation",
            new { expectedVersion = 1, isPreserved = false });
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        Assert.False(retention.Preservations[clip.PublicId!].IsPreserved);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var actions = await database.IamAuditEvents.Select(entry => entry.Action).ToArrayAsync();
        Assert.Contains("clip.preservation.disabled", actions);
        Assert.DoesNotContain("clip.preservation.enabled", actions);
    }

    [Fact]
    public async Task Remote_retention_unavailability_returns_503_without_local_fallback()
    {
        await using var factory = new ApiFactory(monitoringRetentionSource: "remote",
            monitoringRetentionClient: new FakeRetentionClient { Unavailable = true });
        var capture = factory.Services.GetRequiredService<CaptureOptions>();
        var cameraPublicId = await GetCameraPublicIdAsync(factory, capture.CameraId);
        using var admin = await LoginAsync(factory);
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var policy = await admin.GetAsync(
            $"/api/v1/admin/cameras/{cameraPublicId}/clip-retention");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, policy.StatusCode);
        using var saved = await admin.PutAsJsonAsync(
            $"/api/v1/admin/cameras/{cameraPublicId}/clip-retention",
            new { expectedVersion = 0, retentionDays = 15, maximumStorageGigabytes = 10 });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, saved.StatusCode);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.False(await database.IamAuditEvents.AnyAsync(item => item.Action.StartsWith("clip.")));
        Assert.False(await database.ClipRetentionPolicies.AnyAsync());
        Assert.False(await database.ClipPreservations.AnyAsync());
    }

    [Fact]
    public async Task Client_maps_retention_paths_conflicts_and_not_found()
    {
        var handler = new RecordingHandler();
        var client = new MonitoringRetentionClient(new StubFactory(handler),
            new MonitoringModuleOptions(MonitoringModuleOptions.Remote, "http://monitoring.invalid",
                MonitoringModuleOptions.Remote, RetentionSource: MonitoringModuleOptions.Remote));

        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new MonitoringRetentionPolicy("CAM000000000001", 15, 50, 1,
                DateTimeOffset.UtcNow))
        };
        var policy = await client.GetPolicyAsync("CAM000000000001");
        Assert.Equal(15, policy!.RetentionDays);
        Assert.Equal("/api/v1/monitoring/retention/cameras/CAM000000000001", handler.LastPath);

        handler.Response = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new { error = "clip_preservation_version_conflict" })
        };
        var conflict = await Assert.ThrowsAsync<MonitoringRetentionRejectedException>(() =>
            client.SetPreservationAsync("CLP000000000001", 1, true));
        Assert.Equal("clip_preservation_version_conflict", conflict.Message);
        Assert.Equal("/api/v1/monitoring/retention/clips/CLP000000000001/preservation", handler.LastPath);

        handler.Response = new HttpResponseMessage(HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            client.SetPreservationAsync("CLP000000000001", 1, true));

        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new[] { new MonitoringPreservation("CLP000000000001", true, 1) })
        };
        var states = await client.ListPreservationsAsync(["CLP000000000001"]);
        Assert.Single(states);
        Assert.Equal("/api/v1/monitoring/retention/preservations?clipPublicIds=CLP000000000001",
            handler.LastPath);
    }

    private static async Task<string> GetCameraPublicIdAsync(ApiFactory factory, string internalCameraId)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IamDbContext>().CameraCatalog.AsNoTracking()
            .Where(item => item.CameraId == internalCameraId).Select(item => item.PublicId).SingleAsync())!;
    }

    private static async Task<ClipManifestV1> WriteArchivedClipAsync(CaptureOptions capture, string cameraId,
        DateTimeOffset archivedAt)
    {
        var id = Guid.NewGuid().ToString("N");
        var clipPublicId = new PublicIdGenerator().Generate(PublicIdPrefixes.ClipPrefix);
        var clipRoot = Path.Combine(capture.SpoolDirectory, "clips");
        Directory.CreateDirectory(Path.Combine(clipRoot, "media"));
        Directory.CreateDirectory(Path.Combine(clipRoot, "thumbnails"));
        Directory.CreateDirectory(Path.Combine(clipRoot, "manifests"));
        var mediaName = id + ".ts";
        var thumbnailName = id + ".jpg";
        var mediaPath = Path.Combine(clipRoot, "media", mediaName);
        var thumbnailPath = Path.Combine(clipRoot, "thumbnails", thumbnailName);
        await File.WriteAllBytesAsync(mediaPath, [0x47, 0x01, 0x02]);
        await File.WriteAllBytesAsync(thumbnailPath, [0xff, 0xd8, 0xff, 0xd9]);
        var start = archivedAt.AddMinutes(-1);
        var clip = new ClipManifestV1("1.0", id, cameraId, start, start.AddMinutes(1), start,
            start.AddMinutes(1), "ready", "trigger-v1", "visual_change:test", [Guid.NewGuid().ToString("N")],
            mediaName, await HashAsync(mediaPath), thumbnailName, await HashAsync(thumbnailPath), 1, archivedAt,
            ArchiveStatus: "archived", ArchivedAt: archivedAt, PublicId: clipPublicId);
        await File.WriteAllTextAsync(Path.Combine(clipRoot, "manifests", id + ".json"),
            JsonSerializer.Serialize(clip, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return clip;
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
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

    private sealed class FakeRetentionClient : IMonitoringRetentionClient
    {
        public bool Unavailable { get; init; }
        public string? LastCameraId { get; private set; }
        public IReadOnlyList<string> LastPreservationIds { get; private set; } = [];
        public Dictionary<string, MonitoringRetentionPolicy> Policies { get; } = new();
        public Dictionary<string, MonitoringPreservation> Preservations { get; } = new();

        public Task<MonitoringRetentionPolicy?> GetPolicyAsync(string cameraId,
            CancellationToken cancellationToken = default)
        {
            if (Unavailable) throw new MonitoringRetentionUnavailableException();
            LastCameraId = cameraId;
            return Task.FromResult(Policies.TryGetValue(cameraId, out var policy) ? policy : null);
        }

        public Task<MonitoringRetentionPolicy> UpdatePolicyAsync(string cameraId, long expectedVersion,
            int retentionDays, int? maximumStorageGigabytes, CancellationToken cancellationToken = default)
        {
            if (Unavailable) throw new MonitoringRetentionUnavailableException();
            LastCameraId = cameraId;
            var current = Policies.TryGetValue(cameraId, out var existing) ? existing : null;
            if (current is not null && current.Version != expectedVersion)
                throw new MonitoringRetentionRejectedException("clip_retention_version_conflict");
            var updated = new MonitoringRetentionPolicy(cameraId, retentionDays, maximumStorageGigabytes,
                expectedVersion + 1, DateTimeOffset.UtcNow);
            Policies[cameraId] = updated;
            return Task.FromResult(updated);
        }

        public Task<MonitoringPreservation> SetPreservationAsync(string clipPublicId, long expectedVersion,
            bool isPreserved, CancellationToken cancellationToken = default)
        {
            if (Unavailable) throw new MonitoringRetentionUnavailableException();
            var current = Preservations.TryGetValue(clipPublicId, out var existing)
                ? existing
                : new MonitoringPreservation(clipPublicId, false, expectedVersion);
            if (current.Version != expectedVersion)
                throw new MonitoringRetentionRejectedException("clip_preservation_version_conflict");
            var updated = current with { IsPreserved = isPreserved, Version = expectedVersion + 1 };
            Preservations[clipPublicId] = updated;
            return Task.FromResult(updated);
        }

        public Task<IReadOnlyList<MonitoringPreservation>> ListPreservationsAsync(
            IReadOnlyCollection<string> clipPublicIds, CancellationToken cancellationToken = default)
        {
            if (Unavailable) throw new MonitoringRetentionUnavailableException();
            LastPreservationIds = clipPublicIds.ToArray();
            IReadOnlyList<MonitoringPreservation> states = clipPublicIds
                .Where(Preservations.ContainsKey).Select(id => Preservations[id]).ToArray();
            return Task.FromResult(states);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.PathAndQuery;
            return Task.FromResult(Response);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://monitoring.invalid/") };
    }
}
