using System.Net;
using GarageWatch.Api.Correlation;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class CorrelationPropagationHandlerTests
{
    [Fact]
    public async Task Monitoring_requests_receive_the_current_correlation_id()
    {
        var correlation = new StubCorrelation
        {
            CorrelationId = "a1b2c3d4-1111-4222-8333-444455556666"
        };
        var capture = new CaptureHandler();
        using var invoker = new HttpMessageInvoker(new CorrelationPropagationHandler(correlation)
        {
            InnerHandler = capture
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://monitoring/api/v1/monitoring/health");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", "old-value");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(correlation.CorrelationId,
            Assert.Single(capture.Request!.Headers.GetValues("X-Correlation-ID")));
    }

    private sealed class StubCorrelation : ICorrelationContext
    {
        public string CorrelationId { get; set; } = string.Empty;
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
