using System.Net;
using System.Text;
using GarageWatch.Infrastructure.Monitoring;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class MonitoringClipFramesClientTests
{
    [Fact]
    public async Task Manifest_request_widens_the_named_client_timeout_for_face_detection()
    {
        var handler = new StaticHandler(HttpStatusCode.OK,
            """{"clipPublicId":"CLP529251157048","durationMilliseconds":12000,"sampledFrames":1,"frames":[{"index":0,"offsetMilliseconds":1000,"faces":[]}]}""",
            "application/json");
        var factory = new CapturingFactory(handler, TimeSpan.FromSeconds(3));

        var client = new MonitoringClipFramesClient(factory);
        var manifest = await client.GetManifestAsync("CLP529251157048");

        Assert.NotNull(manifest);
        Assert.Equal("CLP529251157048", manifest.ClipPublicId);
        Assert.True(factory.LastClient.Timeout >= TimeSpan.FromSeconds(30),
            "The frames manifest endpoint runs inline face detection before responding and must outlive " +
            "the 3s 'monitoring-module' control-plane timeout; otherwise it surfaces false 503s.");
    }

    [Fact]
    public async Task Frame_request_widens_the_named_client_timeout()
    {
        var handler = new StaticHandler(HttpStatusCode.OK, "jpeg", "image/jpeg");
        var factory = new CapturingFactory(handler, TimeSpan.FromSeconds(3));

        var client = new MonitoringClipFramesClient(factory);
        var image = await client.GetFrameAsync("CLP529251157048", 0);

        Assert.NotNull(image);
        Assert.Equal("image/jpeg", image.ContentType);
        Assert.True(factory.LastClient.Timeout >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Unavailable_manifest_is_reported_as_null()
    {
        var client = new MonitoringClipFramesClient(new CapturingFactory(
            new StaticHandler(HttpStatusCode.ServiceUnavailable, "{}", "application/json"), TimeSpan.FromSeconds(3)));

        Assert.Null(await client.GetManifestAsync("CLP529251157048"));
    }

    private sealed class StaticHandler(HttpStatusCode status, string body, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new StringContent(body, Encoding.UTF8, contentType);
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    private sealed class CapturingFactory(HttpMessageHandler handler, TimeSpan initialTimeout) : IHttpClientFactory
    {
        public HttpClient LastClient { get; private set; } = null!;

        public HttpClient CreateClient(string name)
        {
            LastClient = new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://monitoring/", UriKind.Absolute),
                Timeout = initialTimeout
            };
            return LastClient;
        }
    }
}
