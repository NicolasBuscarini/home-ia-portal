using System.Net;
using System.Net.Http.Json;
using GarageWatch.Application.Monitoring;

namespace GarageWatch.Infrastructure.Monitoring;

public sealed class MonitoringRosterClient(
    IHttpClientFactory httpClientFactory) : IMonitoringRosterClient
{
    public async Task<MonitoringRosterPage> ListAsync(int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.GetAsync(
                $"api/v1/monitoring/roster/people?page={page}&pageSize={pageSize}", cancellationToken);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<MonitoringRosterPage>(cancellationToken)
                  ?? throw new MonitoringRosterUnavailableException()
                : throw new MonitoringRosterUnavailableException();
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
        {
            throw new MonitoringRosterUnavailableException();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MonitoringRosterUnavailableException();
        }
    }

    public async Task<MonitoringRosterPerson> CreateAsync(MonitoringRosterCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.PostAsJsonAsync("api/v1/monitoring/roster/people", request,
                cancellationToken);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<MonitoringRosterPerson>(cancellationToken)
                  ?? throw new MonitoringRosterUnavailableException()
                : throw new MonitoringRosterUnavailableException();
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
        {
            throw new MonitoringRosterUnavailableException();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MonitoringRosterUnavailableException();
        }
    }

    public async Task RemoveAsync(string publicId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.DeleteAsync(
                $"api/v1/monitoring/roster/people/{Uri.EscapeDataString(publicId)}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NoContent) return;
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new KeyNotFoundException("Pessoa não encontrada.");
            throw new MonitoringRosterUnavailableException();
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
        {
            throw new MonitoringRosterUnavailableException();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MonitoringRosterUnavailableException();
        }
    }

}
