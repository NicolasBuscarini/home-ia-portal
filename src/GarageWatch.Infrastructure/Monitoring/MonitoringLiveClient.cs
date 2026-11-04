using GarageWatch.Application.Monitoring;

namespace GarageWatch.Infrastructure.Monitoring;

public sealed class MonitoringLiveClient(
    IHttpClientFactory httpClientFactory) : IMonitoringLiveClient
{
    public async Task<string?> GetPlaylistAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.GetAsync(
                $"api/v1/monitoring/live/{Uri.EscapeDataString(cameraId)}/index.m3u8", cancellationToken);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancellationToken) : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<MonitoringLiveSegment?> GetSegmentAsync(string cameraId, string segmentId, string? range,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            client.Timeout = TimeSpan.FromSeconds(30);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"api/v1/monitoring/live/{Uri.EscapeDataString(cameraId)}/segments/{Uri.EscapeDataString(segmentId)}");
            if (!string.IsNullOrWhiteSpace(range))
                request.Headers.TryAddWithoutValidation("Range", range);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return new MonitoringLiveSegment((int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType ?? "video/mp2t", content,
                response.Content.Headers.ContentRange?.ToString());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}
