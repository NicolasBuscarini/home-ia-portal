using System.Net;
using System.Text;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Monitoring;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class MonitoringModuleClientTests
{
    [Fact]
    public async Task In_process_mode_returns_local_contract_without_http()
    {
        var client = new MonitoringModuleClient(new StubHttpClientFactory(new NeverCalledHandler()),
            new MonitoringModuleOptions(MonitoringModuleOptions.InProcess, null, MonitoringModuleOptions.LocalStatus));
        var contract = await client.GetContractAsync();
        Assert.True(contract.Reachable);
        Assert.Equal("in-process", contract.ExecutionMode);
        Assert.Contains("camera.status", contract.Capabilities);
    }

    [Fact]
    public async Task Remote_mode_reads_the_monitoring_contract()
    {
        var json = """
            {"schemaVersion":"1.0","moduleId":"monitoring","contractVersion":"1.0",
             "executionMode":"contract-only","capabilities":["camera.status","health"],
             "generatedAtUtc":"2026-11-02T12:00:00Z","reachable":false,"errorCode":null}
            """;
        var client = new MonitoringModuleClient(
            new StubHttpClientFactory(new StaticHandler(HttpStatusCode.OK, json)),
            new MonitoringModuleOptions(MonitoringModuleOptions.Remote, "http://monitoring:8080", MonitoringModuleOptions.Remote));
        var contract = await client.GetContractAsync();
        Assert.True(contract.Reachable);
        Assert.Equal("contract-only", contract.ExecutionMode);
        Assert.Equal("monitoring", contract.ModuleId);
    }

    [Fact]
    public async Task Remote_failure_is_sanitized_and_not_reachable()
    {
        var client = new MonitoringModuleClient(
            new StubHttpClientFactory(new StaticHandler(HttpStatusCode.ServiceUnavailable, "{}")),
            new MonitoringModuleOptions(MonitoringModuleOptions.Remote, "http://monitoring:8080", MonitoringModuleOptions.Remote));
        var contract = await client.GetContractAsync();
        Assert.False(contract.Reachable);
        Assert.Equal("monitoring_contract_error", contract.ErrorCode);
    }

    [Fact]
    public async Task Remote_camera_status_reads_the_monitoring_read_model()
    {
        var json = """
            {"schemaVersion":"1.0","moduleId":"monitoring","cameraId":"garage-01","status":"capturing",
             "streamEpoch":"epoch-1","segments":3,"gaps":1,"lastSegmentEndedAtUtc":"2026-11-02T12:00:00Z",
             "reasonCode":null,"reachable":false,"errorCode":null}
            """;
        var client = new MonitoringModuleClient(
            new StubHttpClientFactory(new StaticHandler(HttpStatusCode.OK, json)),
            new MonitoringModuleOptions(MonitoringModuleOptions.Remote, "http://monitoring:8080", MonitoringModuleOptions.Remote));
        var status = await client.GetCameraStatusAsync("garage-01");
        Assert.True(status.Reachable);
        Assert.Equal("capturing", status.Status);
        Assert.Equal("epoch-1", status.StreamEpoch);
        Assert.Equal(3, status.Segments);
    }

    [Fact]
    public async Task Local_camera_status_does_not_call_http()
    {
        var client = new MonitoringModuleClient(new StubHttpClientFactory(new NeverCalledHandler()),
            new MonitoringModuleOptions(MonitoringModuleOptions.InProcess, null, MonitoringModuleOptions.LocalStatus));
        var status = await client.GetCameraStatusAsync("garage-01");
        Assert.True(status.Reachable);
        Assert.Equal("local", status.Status);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://monitoring/", UriKind.Absolute)
        };
    }

    private sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => throw new InvalidOperationException("HTTP must not be called in-process");
    }
}
