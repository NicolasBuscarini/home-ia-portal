using System.Net.Http.Json;
using GarageWatch.Application.Monitoring;

namespace GarageWatch.Infrastructure.Monitoring;

public sealed class MonitoringClipFramesClient(IHttpClientFactory httpClientFactory) : IMonitoringClipFramesClient
{
    private const int MaximumImageBytes = 2 * 1024 * 1024;

    public async Task<MonitoringClipFrameManifest?> GetManifestAsync(string clipPublicId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.GetAsync(
                $"api/v1/monitoring/clips/{Uri.EscapeDataString(clipPublicId)}/frames", cancellationToken);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<MonitoringClipFrameManifest>(cancellationToken)
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public async Task<MonitoringClipFrameImage?> GetFrameAsync(string clipPublicId, int index,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.GetAsync(
                $"api/v1/monitoring/clips/{Uri.EscapeDataString(clipPublicId)}/frames/{index}", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength is > MaximumImageBytes) return null;
            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (content.Length == 0 || content.Length > MaximumImageBytes) return null;
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            return contentType.StartsWith("image/", StringComparison.Ordinal)
                ? new MonitoringClipFrameImage(content, contentType)
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}
