using System.Net.Http.Json;
using GarageWatch.Application.Monitoring;

namespace GarageWatch.Infrastructure.Monitoring;

public sealed class MonitoringClipMediaClient(
    IHttpClientFactory httpClientFactory,
    MonitoringModuleOptions options) : IMonitoringClipMediaClient
{
    public async Task<MonitoringClipMediaResult?> AssembleAsync(MonitoringClipMediaRequest request,
        CancellationToken cancellationToken = default)
    {
        if (options.ClipsWriteSource != MonitoringModuleOptions.Remote) return null;
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            client.Timeout = TimeSpan.FromSeconds(120);
            using var response = await client.PostAsJsonAsync("api/v1/monitoring/clips/media", request,
                cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<MonitoringClipMediaResult>(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
