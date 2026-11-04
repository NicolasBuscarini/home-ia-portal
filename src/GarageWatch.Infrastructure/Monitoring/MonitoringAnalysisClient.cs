using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Application.Monitoring;

namespace GarageWatch.Infrastructure.Monitoring;

public sealed class MonitoringAnalysisClient(
    IHttpClientFactory httpClientFactory) : IMonitoringAnalysisClient
{
    public async Task<MonitoringAnalysisPolicy?> GetPolicyAsync(string cameraId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.GetAsync(
                $"api/v1/monitoring/analysis/policies/{Uri.EscapeDataString(cameraId)}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) throw new MonitoringAnalysisUnavailableException();
            return await response.Content.ReadFromJsonAsync<MonitoringAnalysisPolicy>(cancellationToken)
                ?? throw new MonitoringAnalysisUnavailableException();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            throw new MonitoringAnalysisUnavailableException();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MonitoringAnalysisUnavailableException();
        }
    }

    public async Task<MonitoringAnalysisPolicy?> UpdatePolicyAsync(string cameraId, string contextId,
        long expectedVersion, string policy, string actorId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.PutAsJsonAsync(
                $"api/v1/monitoring/analysis/policies/{Uri.EscapeDataString(cameraId)}",
                new { contextId, expectedVersion, policy, actorId }, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Conflict) throw new MonitoringAnalysisConflictException(
                await ErrorCodeAsync(response, cancellationToken));
            if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException(
                await ErrorCodeAsync(response, cancellationToken));
            if (!response.IsSuccessStatusCode) throw new MonitoringAnalysisUnavailableException();
            return await response.Content.ReadFromJsonAsync<MonitoringAnalysisPolicy>(cancellationToken)
                ?? throw new MonitoringAnalysisUnavailableException();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            throw new MonitoringAnalysisUnavailableException();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MonitoringAnalysisUnavailableException();
        }
    }

    public async Task<MonitoringAnalysisJob?> EnqueueAsync(MonitoringAnalysisEnqueue request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.PostAsJsonAsync("api/v1/monitoring/analysis/jobs", request,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new MonitoringAnalysisCapacityException();
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var code = await ErrorCodeAsync(response, cancellationToken);
                if (code == "analysis_not_allowed") throw new MonitoringAnalysisDeniedException(code);
                throw new MonitoringAnalysisConflictException(code);
            }
            if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException(
                await ErrorCodeAsync(response, cancellationToken));
            if (!response.IsSuccessStatusCode) throw new MonitoringAnalysisUnavailableException();
            return await response.Content.ReadFromJsonAsync<MonitoringAnalysisJob>(cancellationToken)
                ?? throw new MonitoringAnalysisUnavailableException();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            throw new MonitoringAnalysisUnavailableException();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MonitoringAnalysisUnavailableException();
        }
    }

    public async Task<MonitoringAnalysisJob?> ClaimAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.PostAsync("api/v1/monitoring/analysis/jobs/claim", null,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NoContent) return null;
            if (!response.IsSuccessStatusCode) throw new MonitoringAnalysisUnavailableException();
            return await response.Content.ReadFromJsonAsync<MonitoringAnalysisJob>(cancellationToken)
                ?? throw new MonitoringAnalysisUnavailableException();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            throw new MonitoringAnalysisUnavailableException();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MonitoringAnalysisUnavailableException();
        }
    }

    public async Task<IReadOnlyList<MonitoringAnalysisJobStatus>?> LatestForClipsAsync(
        IReadOnlyCollection<string> clipPublicIds, CancellationToken cancellationToken = default)
    {
        if (clipPublicIds.Count is 0 or > 100) return null;
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            var query = string.Join("&", clipPublicIds.Select(id =>
                "clipPublicIds=" + Uri.EscapeDataString(id)));
            using var response = await client.GetAsync("api/v1/monitoring/analysis/clips?" + query,
                cancellationToken);
            if (!response.IsSuccessStatusCode) throw new MonitoringAnalysisUnavailableException();
            return await response.Content.ReadFromJsonAsync<List<MonitoringAnalysisJobStatus>>(cancellationToken)
                ?? throw new MonitoringAnalysisUnavailableException();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            throw new MonitoringAnalysisUnavailableException();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MonitoringAnalysisUnavailableException();
        }
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("error", out var error)
                ? error.GetString() ?? "analysis_request_failed"
                : "analysis_request_failed";
        }
        catch (JsonException)
        {
            return "analysis_request_failed";
        }
    }
}
