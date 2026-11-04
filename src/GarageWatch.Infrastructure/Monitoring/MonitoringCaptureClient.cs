using System.Net.Http.Json;
using GarageWatch.Application.Monitoring;

namespace GarageWatch.Infrastructure.Monitoring;

public sealed class MonitoringCaptureClient(
    IHttpClientFactory httpClientFactory) : IMonitoringCaptureClient
{
    public Task<MonitoringCaptureSnapshot?> GetCaptureAsync(CancellationToken cancellationToken = default) =>
        GetAsync("api/v1/monitoring/capture", cancellationToken);

    public Task<MonitoringCaptureSettings?> UpdateCaptureAsync(MonitoringCaptureSettings settings,
        CancellationToken cancellationToken = default) =>
        PutAsync("api/v1/monitoring/capture/settings", settings, cancellationToken);

    private async Task<MonitoringCaptureSnapshot?> GetAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.GetAsync(path, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<MonitoringCaptureSnapshot>(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<MonitoringCaptureSettings?> PutAsync(string path, MonitoringCaptureSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.PutAsJsonAsync(path, new
            {
                enabled = settings.Enabled,
                segmentSeconds = settings.SegmentSeconds,
                maximumSegments = settings.MaximumSegments,
                maximumSegmentBytes = settings.MaximumSegmentBytes
            }, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<MonitoringCaptureSettings>(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
