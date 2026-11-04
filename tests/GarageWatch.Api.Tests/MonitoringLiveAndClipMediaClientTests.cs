using System.Net;
using System.Text;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Monitoring;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class MonitoringLiveAndClipMediaClientTests
{
    [Fact]
    public async Task Remote_live_client_reads_the_playlist_and_forwards_segment_ranges()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.EndsWith("index.m3u8")
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("#EXTM3U\nsegments/abc\n", Encoding.UTF8, "application/vnd.apple.mpegurl")
            }
            : SegmentResponse());
        var client = new MonitoringLiveClient(new StubFactory(handler));

        var playlist = await client.GetPlaylistAsync("garage-01");
        Assert.NotNull(playlist);
        Assert.Contains("#EXTM3U", playlist);
        Assert.Equal("/api/v1/monitoring/live/garage-01/index.m3u8", handler.Requests[0].Uri);

        var segment = await client.GetSegmentAsync("garage-01", "abc", "bytes=0-2");
        Assert.NotNull(segment);
        Assert.Equal(206, segment.StatusCode);
        Assert.Equal("video/mp2t", segment.ContentType);
        Assert.Equal("bytes 0-2/3", segment.ContentRange);
        Assert.Equal(3, segment.Content.Length);
        Assert.Equal("bytes=0-2", handler.Requests[1].Range);
    }

    [Fact]
    public async Task Unreachable_live_service_is_reported_as_unavailable()
    {
        var client = new MonitoringLiveClient(new StubFactory(new FailingHandler()));
        Assert.Null(await client.GetPlaylistAsync("garage-01"));
    }

    [Fact]
    public async Task Remote_clip_media_client_posts_the_segment_scope_and_reads_checksums()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"mediaFileName":"abc.ts","mediaSha256":"aa","thumbnailFileName":"abc.jpg","thumbnailSha256":"bb"}""",
                Encoding.UTF8, "application/json")
        });
        var client = new MonitoringClipMediaClient(new StubFactory(handler));
        var result = await client.AssembleAsync(new MonitoringClipMediaRequest("abc", "garage-01",
            ["one", "two"], DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow));

        Assert.NotNull(result);
        Assert.Equal("abc.ts", result.MediaFileName);
        Assert.Equal("bb", result.ThumbnailSha256);
        Assert.Equal("/api/v1/monitoring/clips/media", handler.Requests[0].Uri);
        Assert.Contains("\"segmentIds\":[\"one\",\"two\"]", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Remote_clip_media_assembler_fails_observably_when_the_service_is_unavailable()
    {
        var assembler = new RemoteClipMediaAssembler(new UnavailableClipMediaClient());
        var clip = new ClipManifestV1("1.0", "abc", "garage-01", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "collecting", "1", "motion",
            ["one"], null, null, null, null, 0, DateTimeOffset.UtcNow);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            assembler.AssembleAsync(clip, ["/tmp/one.ts"], CancellationToken.None));
        Assert.Equal("monitoring_clip_media_unavailable", error.Message);
    }

    [Fact]
    public void Base_url_is_required_and_validated()
    {
        var missing = new ConfigurationBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => MonitoringModuleOptions.FromConfiguration(missing));

        var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MONITORING_BASE_URL"] = "file:///tmp/monitoring"
        }).Build();
        Assert.Throws<InvalidOperationException>(() => MonitoringModuleOptions.FromConfiguration(invalid));

        var valid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MONITORING_BASE_URL"] = "http://monitoring:8080/"
        }).Build();
        Assert.Equal("http://monitoring:8080", MonitoringModuleOptions.FromConfiguration(valid).BaseUrl);
    }

    private static HttpResponseMessage SegmentResponse()
    {
        var content = new ByteArrayContent([0x47, 0x40, 0x11]);
        content.Headers.ContentType = new("video/mp2t");
        content.Headers.TryAddWithoutValidation("Content-Range", "bytes 0-2/3");
        return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
    }

    private sealed record RecordedRequest(string Uri, string Body, string? Range);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.RequestUri!.AbsolutePath, body,
                request.Headers.TryGetValues("Range", out var values) ? values.FirstOrDefault() : null));
            return respond(request);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://monitoring/", UriKind.Absolute)
        };
    }

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => throw new InvalidOperationException("HTTP must not be called locally");
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => throw new HttpRequestException("unreachable");
    }

    private sealed class UnavailableClipMediaClient : IMonitoringClipMediaClient
    {
        public Task<MonitoringClipMediaResult?> AssembleAsync(MonitoringClipMediaRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult<MonitoringClipMediaResult?>(null);
    }
}
