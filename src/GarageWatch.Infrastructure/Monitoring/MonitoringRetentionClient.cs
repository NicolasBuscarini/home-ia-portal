using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Application.Monitoring;

namespace GarageWatch.Infrastructure.Monitoring;

public sealed class MonitoringRetentionClient(
    IHttpClientFactory httpClientFactory,
    MonitoringModuleOptions options) : IMonitoringRetentionClient
{
    public async Task<MonitoringRetentionPolicy?> GetPolicyAsync(string cameraId,
        CancellationToken cancellationToken = default)
    {
        EnsureRemote();
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.GetAsync(
                $"api/v1/monitoring/retention/cameras/{Uri.EscapeDataString(cameraId)}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<MonitoringRetentionPolicy>(cancellationToken)
                : throw new MonitoringRetentionUnavailableException();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            throw new MonitoringRetentionUnavailableException();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MonitoringRetentionUnavailableException();
        }
    }

    public async Task<MonitoringRetentionPolicy> UpdatePolicyAsync(string cameraId, long expectedVersion,
        int retentionDays, int? maximumStorageGigabytes, CancellationToken cancellationToken = default)
    {
        EnsureRemote();
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.PutAsJsonAsync(
                $"api/v1/monitoring/retention/cameras/{Uri.EscapeDataString(cameraId)}",
                new { expectedVersion, retentionDays, maximumStorageGigabytes }, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            return await response.Content.ReadFromJsonAsync<MonitoringRetentionPolicy>(cancellationToken)
                   ?? throw new MonitoringRetentionUnavailableException();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            throw new MonitoringRetentionUnavailableException();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MonitoringRetentionUnavailableException();
        }
    }

    public async Task<MonitoringPreservation> SetPreservationAsync(string clipPublicId, long expectedVersion,
        bool isPreserved, CancellationToken cancellationToken = default)
    {
        EnsureRemote();
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.PutAsJsonAsync(
                $"api/v1/monitoring/retention/clips/{Uri.EscapeDataString(clipPublicId)}/preservation",
                new { expectedVersion, isPreserved }, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            return await response.Content.ReadFromJsonAsync<MonitoringPreservation>(cancellationToken)
                   ?? throw new MonitoringRetentionUnavailableException();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            throw new MonitoringRetentionUnavailableException();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MonitoringRetentionUnavailableException();
        }
    }

    public async Task<IReadOnlyList<MonitoringPreservation>> ListPreservationsAsync(
        IReadOnlyCollection<string> clipPublicIds, CancellationToken cancellationToken = default)
    {
        EnsureRemote();
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            var query = string.Join("&", clipPublicIds.Select(id =>
                "clipPublicIds=" + Uri.EscapeDataString(id)));
            using var response = await client.GetAsync(
                $"api/v1/monitoring/retention/preservations?{query}", cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            return await response.Content.ReadFromJsonAsync<MonitoringPreservation[]>(cancellationToken)
                   ?? throw new MonitoringRetentionUnavailableException();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            throw new MonitoringRetentionUnavailableException();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MonitoringRetentionUnavailableException();
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.NotFound) throw new KeyNotFoundException();
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("invalid_retention_request");
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var code = "clip_retention_conflict";
            try
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (body.RootElement.TryGetProperty("error", out var error) &&
                    error.GetString() is { Length: > 0 } parsed) code = parsed;
            }
            catch (JsonException) { }
            throw new MonitoringRetentionRejectedException(code);
        }
        throw new MonitoringRetentionUnavailableException();
    }

    private void EnsureRemote()
    {
        if (options.RetentionSource != MonitoringModuleOptions.Remote)
            throw new MonitoringRetentionUnavailableException();
    }
}
