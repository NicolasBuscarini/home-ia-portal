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
        if (query.PublicId is not null)
            parameters.Add($"publicId={Uri.EscapeDataString(query.PublicId)}");
        if (query.MinDurationSeconds.HasValue)
            parameters.Add($"minDurationSeconds={query.MinDurationSeconds.Value}");
        if (query.MaxDurationSeconds.HasValue)
            parameters.Add($"maxDurationSeconds={query.MaxDurationSeconds.Value}");
        if (query.Analysis is not null)
            parameters.Add($"analysis={Uri.EscapeDataString(query.Analysis)}");
        if (query.Preserved.HasValue)
            parameters.Add($"preserved={(query.Preserved.Value ? "true" : "false")}");
        if (query.Sort is not null)
            parameters.Add($"sort={Uri.EscapeDataString(query.Sort)}");
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

    public async Task<MonitoringClipDeleteResult> DeleteAsync(string publicId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.DeleteAsync(
                $"api/v1/monitoring/clips/{Uri.EscapeDataString(publicId)}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NoContent)
                return new(MonitoringClipDeleteStatus.Deleted);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(MonitoringClipDeleteStatus.NotFound, "clip_not_found");
            var error = await response.Content.ReadFromJsonAsync<DeleteError>(cancellationToken);
            return error?.Error switch
            {
                "clip_preserved" => new(MonitoringClipDeleteStatus.Preserved, error.Error),
                "clip_archived" => new(MonitoringClipDeleteStatus.Archived, error.Error),
                "clip_not_ready" => new(MonitoringClipDeleteStatus.NotReady, error.Error),
                _ => new(MonitoringClipDeleteStatus.Failed, error?.Error ?? "clip_delete_failed")
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
        {
            return new(MonitoringClipDeleteStatus.Unavailable, "clip_delete_unavailable");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(MonitoringClipDeleteStatus.Unavailable, "clip_delete_unavailable");
        }
    }

    public async Task<MonitoringClipDeleteAllResult?> DeleteAllAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.PostAsync("api/v1/monitoring/clips/delete-all", null,
                cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<MonitoringClipDeleteAllResult>(cancellationToken);
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

    private sealed record DeleteError(string? Error);

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
