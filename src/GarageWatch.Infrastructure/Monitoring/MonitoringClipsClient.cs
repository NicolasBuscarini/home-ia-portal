using System.Net;
using System.Net.Http.Json;
using GarageWatch.Application.Monitoring;

namespace GarageWatch.Infrastructure.Monitoring;

public sealed class MonitoringClipsClient(
    IHttpClientFactory httpClientFactory) : IMonitoringClipsClient
{
    public async Task<MonitoringClipPage?> ListAsync(MonitoringClipQuery query,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>
        {
            $"page={query.Page}", $"pageSize={query.PageSize}"
        };
        parameters.AddRange(query.CameraIds.Select(cameraId =>
            $"cameraIds={Uri.EscapeDataString(cameraId)}"));
        if (query.FromUtc.HasValue)
            parameters.Add($"fromUtc={Uri.EscapeDataString(query.FromUtc.Value.ToString("O"))}");
        if (query.ToUtc.HasValue)
            parameters.Add($"toUtc={Uri.EscapeDataString(query.ToUtc.Value.ToString("O"))}");
        if (query.State is not null)
            parameters.Add($"state={Uri.EscapeDataString(query.State)}");
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.GetAsync("api/v1/monitoring/clips?" + string.Join('&', parameters),
                cancellationToken);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<MonitoringClipPage>(cancellationToken)
                : null;
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

    public async Task<MonitoringClipItem?> GetAsync(string publicId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.GetAsync(
                $"api/v1/monitoring/clips/{Uri.EscapeDataString(publicId)}", cancellationToken);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<MonitoringClipItem>(cancellationToken)
                : null;
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

    public Task<HttpResponseMessage?> GetThumbnailAsync(string publicId,
        CancellationToken cancellationToken = default) =>
        GetFileAsync($"api/v1/monitoring/clips/{Uri.EscapeDataString(publicId)}/thumbnail", null,
            cancellationToken);

    public Task<HttpResponseMessage?> GetMediaAsync(string publicId, string? range,
        CancellationToken cancellationToken = default) =>
        GetFileAsync($"api/v1/monitoring/clips/{Uri.EscapeDataString(publicId)}/media", range,
            cancellationToken);

    private async Task<HttpResponseMessage?> GetFileAsync(string path, string? range,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient("monitoring-module");
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (!string.IsNullOrWhiteSpace(range) &&
                System.Net.Http.Headers.RangeHeaderValue.TryParse(range, out var parsed))
                request.Headers.Range = parsed;
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            return response;
        }
        catch (Exception exception) when (exception is HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
