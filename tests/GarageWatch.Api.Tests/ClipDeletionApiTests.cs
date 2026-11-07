using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class ClipDeletionApiTests
{
    [Fact]
    public async Task Admin_deletes_clip_through_the_monitoring_service_and_audits_it()
    {
        var remote = new FakeClipsClient(MonitoringClipDeleteStatus.Deleted);
        await using var factory = new ApiFactory(monitoringClipsClient: remote);
        remote.CameraId = factory.Services.GetRequiredService<CaptureOptions>().CameraId;
        using var client = await LoginAsync(factory);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var response = await client.PostAsJsonAsync("/api/v1/clips/delete",
            new { clipIds = new[] { "CLP000000000001" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CLP000000000001", body.GetProperty("deleted")[0].GetString());
        Assert.Empty(body.GetProperty("failed").EnumerateArray());
        using var scope = factory.Services.CreateScope();
        var recorded = await scope.ServiceProvider.GetRequiredService<IamDbContext>().PlatformEvents
            .AsNoTracking().SingleAsync(item => item.EventType == "clip.deleted");
        Assert.Equal("CLP000000000001", recorded.ResourceId);
        Assert.Equal("clip", recorded.ResourceType);
    }

    [Fact]
    public async Task Preserved_clip_is_reported_and_not_marked_as_deleted()
    {
        var remote = new FakeClipsClient(MonitoringClipDeleteStatus.Preserved);
        await using var factory = new ApiFactory(monitoringClipsClient: remote);
        remote.CameraId = factory.Services.GetRequiredService<CaptureOptions>().CameraId;
        using var client = await LoginAsync(factory);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var response = await client.PostAsJsonAsync("/api/v1/clips/delete",
            new { clipIds = new[] { "CLP000000000002" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("deleted").EnumerateArray());
        Assert.Equal("clip_preserved", body.GetProperty("failed")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Empty_selection_is_rejected()
    {
        await using var factory = new ApiFactory();
        using var client = await LoginAsync(factory);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var response = await client.PostAsJsonAsync("/api/v1/clips/delete", new { clipIds = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Administrator_deletes_all_eligible_clips_and_audits_aggregate_counts()
    {
        var remote = new FakeClipsClient(MonitoringClipDeleteStatus.Deleted)
        {
            DeleteAllResult = new MonitoringClipDeleteAllResult(12, 2, 1, 3, 0, 0)
        };
        await using var factory = new ApiFactory(monitoringClipsClient: remote);
        using var client = await LoginAsync(factory);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var response = await client.PostAsync("/api/v1/clips/delete-all", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(12, body.GetProperty("deleted").GetInt32());
        Assert.Equal(2, body.GetProperty("skippedPreserved").GetInt32());
        Assert.Equal(1, body.GetProperty("skippedArchived").GetInt32());
        Assert.Equal(3, body.GetProperty("skippedNotReady").GetInt32());
        Assert.Equal(0, body.GetProperty("skippedActiveAnalysis").GetInt32());
        Assert.Equal(0, body.GetProperty("failed").GetInt32());
        Assert.Equal(1, remote.DeleteAllCalls);
        using var scope = factory.Services.CreateScope();
        var recorded = await scope.ServiceProvider.GetRequiredService<IamDbContext>().PlatformEvents
            .AsNoTracking().SingleAsync(item => item.EventType == "clip.delete.all");
        Assert.Contains("\"deleted\":12", recorded.MetadataJson);
        Assert.Contains("\"skippedPreserved\":2", recorded.MetadataJson);
        Assert.Null(recorded.ResourceId);
    }

    [Fact]
    public async Task Non_administrator_cannot_delete_all_clips()
    {
        var remote = new FakeClipsClient(MonitoringClipDeleteStatus.Deleted);
        await using var factory = new ApiFactory(monitoringClipsClient: remote);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "delete-user", IsActive = true };
            Assert.True((await users.CreateAsync(user, "Delete-User-Password-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, "User")).Succeeded);
        }
        using var client = await LoginAsync(factory, "delete-user", "Delete-User-Password-123!");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var response = await client.PostAsync("/api/v1/clips/delete-all", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, remote.DeleteAllCalls);
    }

    private static async Task<HttpClient> LoginAsync(ApiFactory factory, string username = "contract-user",
        string password = "Contract-Password-123!")
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username, password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        return client;
    }

    private sealed class FakeClipsClient(MonitoringClipDeleteStatus status) : IMonitoringClipsClient
    {
        public string CameraId { get; set; } = "garage-01";
        public MonitoringClipDeleteAllResult DeleteAllResult { get; set; } = new(0, 0, 0, 0, 0, 0);
        public int DeleteAllCalls { get; private set; }
        private MonitoringClipItem Item => new("CLP000000000001", "clip-1", CameraId,
            DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow, "ready", "not_requested",
            "test", "trigger-v1", null, null, 0, DateTimeOffset.UtcNow, 1024);

        public Task<MonitoringClipPage?> ListAsync(MonitoringClipQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MonitoringClipPage?>(new MonitoringClipPage([Item], 1, 25, 1));

        public Task<MonitoringClipItem?> GetAsync(string publicId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MonitoringClipItem?>(Item with { PublicId = publicId });

        public Task<MonitoringClipDeleteResult> DeleteAsync(string publicId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MonitoringClipDeleteResult(status,
                status == MonitoringClipDeleteStatus.Preserved ? "clip_preserved" : null));

        public Task<MonitoringClipDeleteAllResult?> DeleteAllAsync(
            CancellationToken cancellationToken = default)
        {
            DeleteAllCalls++;
            return Task.FromResult<MonitoringClipDeleteAllResult?>(DeleteAllResult);
        }

        public Task<HttpResponseMessage?> GetThumbnailAsync(string publicId,
            CancellationToken cancellationToken = default) => Task.FromResult<HttpResponseMessage?>(null);

        public Task<HttpResponseMessage?> GetMediaAsync(string publicId, string? range,
            CancellationToken cancellationToken = default) => Task.FromResult<HttpResponseMessage?>(null);
    }
}
