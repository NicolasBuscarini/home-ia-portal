using System.Net;
using System.Net.Http.Json;
using GarageWatch.Application.Monitoring;

namespace GarageWatch.Infrastructure.Monitoring;

public sealed class MonitoringModuleClient(
    IHttpClientFactory httpClientFactory) : IMonitoringModuleClient
{
    public async Task<MonitoringModuleContract> GetContractAsync(CancellationToken cancellationToken = default)
    {
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

    public async Task<MonitoringCameraStatus> GetCameraStatusAsync(string cameraId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.GetAsync(
                $"api/v1/monitoring/cameras/{Uri.EscapeDataString(cameraId)}/status", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return UnreachableStatus(cameraId, "camera_not_configured");
            if (!response.IsSuccessStatusCode)
                return UnreachableStatus(cameraId, "monitoring_status_error");
            var status = await response.Content.ReadFromJsonAsync<MonitoringCameraStatus>(cancellationToken);
            return status is null
                ? UnreachableStatus(cameraId, "monitoring_status_invalid")
                : status with { Reachable = true, ErrorCode = null };
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
        {
            return UnreachableStatus(cameraId, "monitoring_unreachable");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UnreachableStatus(cameraId, "monitoring_unreachable");
        }
    }

    private static MonitoringModuleContract Unreachable(string errorCode) =>
        new("1.0", "monitoring", "1.0", "remote", [], null, false, errorCode);

    private static MonitoringCameraStatus UnreachableStatus(string cameraId, string errorCode) =>
        new("1.0", "monitoring", cameraId, "unreachable", null, 0, 0, null, errorCode, false, errorCode);
}
