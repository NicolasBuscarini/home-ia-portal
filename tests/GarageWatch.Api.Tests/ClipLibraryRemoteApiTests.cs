using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class ClipLibraryRemoteApiTests
{
    [Fact]
    public async Task Remote_clips_use_the_authorized_camera_scope_and_proxy_media_with_ranges()
    {
        var internalCameraId = "garage-01";
        var clipId = "0123456789abcdef0123456789abcdef";
        var publicId = "CLP000000000001";
        var fake = new FakeClipsClient(null);
        await using var factory = new ApiFactory(monitoringClipsSource: "remote", monitoringClipsClient: fake);
        using var anonymous = factory.CreateClient();
        internalCameraId = factory.Services.GetRequiredService<GarageWatch.Infrastructure.Capture.CaptureOptions>().CameraId;
        fake.Page = new MonitoringClipPage(
        [
            new MonitoringClipItem(publicId, clipId, internalCameraId, DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow, "ready", "not_requested", "motion", "1", null, null, 0,
                DateTimeOffset.UtcNow, 1_048_576)
        ], 1, 25, 1);
        fake.PublicCameraId = await GetCameraPublicIdAsync(factory, internalCameraId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/clips")).StatusCode);

        using var admin = await LoginAsync(factory);
        using var page = await admin.GetAsync("/api/v1/clips?page=1&pageSize=25");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var body = await page.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Equal(publicId, body.GetProperty("items")[0].GetProperty("publicId").GetString());
        Assert.Equal(fake.PublicCameraId, body.GetProperty("items")[0].GetProperty("cameraId").GetString());
        Assert.Equal(internalCameraId, Assert.Single(fake.LastQuery!.CameraIds));

        using var detail = await admin.GetAsync($"/api/v1/clips/{publicId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailBody = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"/api/v1/clips/{publicId}/media",
            detailBody.GetProperty("mediaUrl").GetString());

        using var thumbnail = await admin.GetAsync($"/api/v1/clips/{publicId}/thumbnail");
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        Assert.Equal("image/jpeg", thumbnail.Content.Headers.ContentType?.MediaType);

        admin.DefaultRequestHeaders.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 63);
        using var media = await admin.GetAsync($"/api/v1/clips/{publicId}/media");
        Assert.Equal(HttpStatusCode.PartialContent, media.StatusCode);
        Assert.Equal("video/mp4", media.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bytes 0-63/128", media.Content.Headers.ContentRange?.ToString());
    }

    [Fact]
    public async Task Remote_clips_report_analysis_state_from_the_service()
    {
        var clipId = "0123456789abcdef0123456789abcdef";
        var publicId = "CLP000000000002";
        var fake = new FakeClipsClient(null);
        var analysis = new FakeAnalysisStatusClient();
        await using var factory = new ApiFactory(monitoringClipsSource: "remote", monitoringClipsClient: fake,
            monitoringAnalysisSource: "remote", monitoringAnalysisClient: analysis);
        using var admin = await LoginAsync(factory);
        var internalCameraId = factory.Services
            .GetRequiredService<GarageWatch.Infrastructure.Capture.CaptureOptions>().CameraId;
        fake.Page = new MonitoringClipPage(
        [
            new MonitoringClipItem(publicId, clipId, internalCameraId, DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow, "ready", "not_requested", "motion", "1", null, null, 0,
                DateTimeOffset.UtcNow, 1024)
        ], 1, 25, 1);
        analysis.States = [new MonitoringAnalysisJobStatus(publicId, "completed", "completed")];

        using var page = await admin.GetAsync("/api/v1/clips");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var body = await page.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", body.GetProperty("items")[0].GetProperty("analysisStatus").GetString());
        Assert.Contains(publicId, analysis.LastClipIds);

        using var detail = await admin.GetAsync($"/api/v1/clips/{publicId}");
        var detailBody = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", detailBody.GetProperty("clip").GetProperty("analysisStatus").GetString());
    }

    private sealed class FakeAnalysisStatusClient : IMonitoringAnalysisClient
    {
        public IReadOnlyList<string> LastClipIds { get; private set; } = [];
        public IReadOnlyList<MonitoringAnalysisJobStatus> States { get; set; } = [];

        public Task<IReadOnlyList<MonitoringAnalysisJobStatus>?> LatestForClipsAsync(
            IReadOnlyCollection<string> clipPublicIds, CancellationToken cancellationToken = default)
        {
            LastClipIds = clipPublicIds.ToArray();
            return Task.FromResult<IReadOnlyList<MonitoringAnalysisJobStatus>?>(States
                .Where(state => clipPublicIds.Contains(state.ClipPublicId)).ToArray());
        }

        public Task<MonitoringAnalysisPolicy?> GetPolicyAsync(string cameraId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<MonitoringAnalysisPolicy?> UpdatePolicyAsync(string cameraId, string contextId,
            long expectedVersion, string policy, string actorId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<MonitoringAnalysisJob?> EnqueueAsync(MonitoringAnalysisEnqueue request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<MonitoringAnalysisJob?> ClaimAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Unreachable_remote_clip_library_returns_service_unavailable()
    {
        await using var factory = new ApiFactory(monitoringClipsSource: "remote",
            monitoringClipsClient: new FakeClipsClient(null));
        using var admin = await LoginAsync(factory);
        using var response = await admin.GetAsync("/api/v1/clips");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private static async Task<string> GetCameraPublicIdAsync(ApiFactory factory, string internalCameraId)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IamDbContext>().CameraCatalog.AsNoTracking()
            .Where(item => item.CameraId == internalCameraId).Select(item => item.PublicId).SingleAsync())!;
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

    private sealed class FakeClipsClient(MonitoringClipPage? page) : IMonitoringClipsClient
    {
        public MonitoringClipQuery? LastQuery { get; private set; }
        public string? PublicCameraId { get; set; }
        public MonitoringClipPage? Page { get; set; } = page;

        public Task<MonitoringClipPage?> ListAsync(MonitoringClipQuery query,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            if (Page is null) return Task.FromResult<MonitoringClipPage?>(null);
            var items = Page.Items.Select(item => item with
            {
                ThumbnailSha256 = new string('a', 64)
            }).ToArray();
            return Task.FromResult<MonitoringClipPage?>(Page with { Items = items });
        }

        public Task<MonitoringClipItem?> GetAsync(string publicId, CancellationToken cancellationToken = default)
        {
            var item = Page?.Items.SingleOrDefault(candidate => candidate.PublicId == publicId);
            return Task.FromResult(item is null ? null : item with { ThumbnailSha256 = new string('a', 64) });
        }

        public Task<HttpResponseMessage?> GetThumbnailAsync(string publicId,
            CancellationToken cancellationToken = default)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0])
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult<HttpResponseMessage?>(response);
        }

        public Task<HttpResponseMessage?> GetMediaAsync(string publicId, string? range,
            CancellationToken cancellationToken = default)
        {
            var bytes = new byte[128];
            Encoding.ASCII.GetBytes("ftyp").CopyTo(bytes, 4);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(bytes, 0, 64)
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(0, 63, 128);
            return Task.FromResult<HttpResponseMessage?>(response);
        }
    }
}
