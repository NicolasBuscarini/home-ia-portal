using System.Net.Http.Json;
using GarageWatch.Application.Monitoring;

namespace GarageWatch.Infrastructure.Monitoring;

public sealed class MonitoringModuleClient(
    IHttpClientFactory httpClientFactory,
    MonitoringModuleOptions options) : IMonitoringModuleClient
{
    private static readonly string[] InProcessCapabilities = ["camera.status", "health", "media.contract"];

    public async Task<MonitoringModuleContract> GetContractAsync(CancellationToken cancellationToken = default)
    {
        if (options.Mode == MonitoringModuleOptions.InProcess)
            return new MonitoringModuleContract("1.0", "monitoring", "1.0", "in-process",
                InProcessCapabilities, DateTimeOffset.UtcNow, true, null);

        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.GetAsync("api/v1/monitoring/contract", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Unreachable("monitoring_contract_error");
            var contract = await response.Content.ReadFromJsonAsync<MonitoringModuleContract>(cancellationToken);
            return contract is null
                ? Unreachable("monitoring_contract_invalid")
                : contract with { Reachable = true, ErrorCode = null };
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
        {
            return Unreachable("monitoring_unreachable");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unreachable("monitoring_unreachable");
        }
    }

    private static MonitoringModuleContract Unreachable(string errorCode) =>
        new("1.0", "monitoring", "1.0", "remote", [], null, false, errorCode);
}
