using System.Net;
using System.Net.Http.Json;
using GarageWatch.Application.Monitoring;

namespace GarageWatch.Infrastructure.Monitoring;

public sealed class MonitoringEventsClient(
    IHttpClientFactory httpClientFactory) : IMonitoringEventsClient
{
    public async Task<MonitoringEventAppendOutcome?> AppendAsync(MonitoringEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.PostAsJsonAsync("api/v1/monitoring/events", envelope,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new MonitoringEventAppendOutcome("capacity_reached", true);
            if (response.StatusCode == HttpStatusCode.Conflict)
                throw new InvalidDataException("event_id_conflict");
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("monitoring_event_append_failed");
            var result = await response.Content.ReadFromJsonAsync<MonitoringEventAppendResult>(cancellationToken);
            return result is null
                ? throw new HttpRequestException("monitoring_event_append_invalid")
                : new MonitoringEventAppendOutcome(result.Status, false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public async Task<MonitoringEventRecovery?> GetRecoveryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.GetAsync("api/v1/monitoring/events/recovery", cancellationToken);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<MonitoringEventRecovery>(cancellationToken)
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

    private sealed record MonitoringEventAppendResult(string Status, string EventId, string EventSha256);
}
