using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Application.Monitoring;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class MonitoringCaptureApiTests
{
    [Fact]
    public async Task Administrator_reads_and_updates_remote_capture_settings()
    {
        var client = new FakeCaptureClient();
        await using var factory = new ApiFactory(monitoringMode: "remote",
            monitoringStatusSource: "remote", monitoringCaptureClient: client);
        using var admin = await LoginAsync(factory);
        using var read = await admin.GetAsync("/api/v1/admin/monitoring/capture");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readJson = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal("ffmpeg-rtsp", readJson.RootElement.GetProperty("profile").GetString());
        Assert.Equal("capturing", readJson.RootElement.GetProperty("state").GetString());
        Assert.True(readJson.RootElement.GetProperty("enabled").GetBoolean());

        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var update = await admin.PutAsJsonAsync("/api/v1/admin/monitoring/capture/settings",
            new { enabled = false, segmentSeconds = 3, maximumSegments = 60, maximumSegmentBytes = 33_554_432 });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var updateJson = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
        Assert.False(updateJson.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(3, updateJson.RootElement.GetProperty("segmentSeconds").GetInt32());
        Assert.Equal(3, updateJson.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(3, client.LastUpdate?.SegmentSeconds);
        Assert.False(client.LastUpdate?.Enabled);
    }

    [Fact]
    public async Task Invalid_limits_are_rejected_before_calling_the_service()
    {
        var client = new FakeCaptureClient();
        await using var factory = new ApiFactory(monitoringMode: "remote",
            monitoringStatusSource: "remote", monitoringCaptureClient: client);
        using var admin = await LoginAsync(factory);
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var response = await admin.PutAsJsonAsync("/api/v1/admin/monitoring/capture/settings",
            new { enabled = true, segmentSeconds = 0, maximumSegments = 60, maximumSegmentBytes = 33_554_432 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(client.LastUpdate);
    }

    [Fact]
    public async Task Local_mode_returns_conflict_instead_of_fake_capture_settings()
    {
        await using var factory = new ApiFactory();
        using var admin = await LoginAsync(factory);
        using var response = await admin.GetAsync("/api/v1/admin/monitoring/capture");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "Contract-Password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        return client;
    }

    private sealed class FakeCaptureClient : IMonitoringCaptureClient
    {
        private MonitoringCaptureSettings _settings = new(true, 5, 120, 67_108_864, 2);
        public MonitoringCaptureSettings? LastUpdate { get; private set; }

        public Task<MonitoringCaptureSnapshot?> GetCaptureAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<MonitoringCaptureSnapshot?>(new MonitoringCaptureSnapshot("1.0", "ffmpeg-rtsp",
                "garage-01", _settings.Enabled, _settings.SegmentSeconds, _settings.MaximumSegments,
                _settings.MaximumSegmentBytes, _settings.Revision, "capturing", "ffmpeg-rtsp-h264", null, 0, 12));

        public Task<MonitoringCaptureSettings?> UpdateCaptureAsync(MonitoringCaptureSettings settings,
            CancellationToken cancellationToken = default)
        {
            LastUpdate = settings;
            _settings = settings with { Revision = _settings.Revision + 1 };
            return Task.FromResult<MonitoringCaptureSettings?>(_settings);
        }
    }
}
