using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Application.Monitoring;

namespace GarageWatch.Infrastructure.Monitoring;

public sealed class MonitoringBiometricClient(
    IHttpClientFactory httpClientFactory) : IMonitoringBiometricClient
{
    public async Task<IReadOnlyList<MonitoringBiometricEnrollment>> ListAsync(string userPublicId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.GetAsync(
                $"api/v1/monitoring/biometrics/enrollments?userPublicId={Uri.EscapeDataString(userPublicId)}",
                cancellationToken);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<MonitoringBiometricEnrollment[]>(cancellationToken)
                       ?? throw new MonitoringBiometricsUnavailableException();
            if (response.StatusCode == HttpStatusCode.BadRequest)
                throw new MonitoringBiometricsRejectedException("invalid_biometric_query");
            throw new MonitoringBiometricsUnavailableException();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            throw new MonitoringBiometricsUnavailableException();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MonitoringBiometricsUnavailableException();
        }
    }

    public async Task<MonitoringBiometricEnrollment> GetAsync(string publicId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.GetAsync(
                $"api/v1/monitoring/biometrics/enrollments/{Uri.EscapeDataString(publicId)}", cancellationToken);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<MonitoringBiometricEnrollment>(cancellationToken)
                       ?? throw new MonitoringBiometricsUnavailableException();
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new KeyNotFoundException("Cadastro biométrico não encontrado.");
            if (response.StatusCode == HttpStatusCode.BadRequest)
                throw new MonitoringBiometricsRejectedException("invalid_biometric_enrollment");
            throw new MonitoringBiometricsUnavailableException();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            throw new MonitoringBiometricsUnavailableException();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MonitoringBiometricsUnavailableException();
        }
    }

    public async Task<MonitoringBiometricEnrollmentResult> EnrollAsync(
        MonitoringBiometricEnrollRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.PostAsJsonAsync("api/v1/monitoring/biometrics/enrollments", request,
                cancellationToken);
            if (response.IsSuccessStatusCode)
                return await response.Content
                           .ReadFromJsonAsync<MonitoringBiometricEnrollmentResult>(cancellationToken)
                       ?? throw new MonitoringBiometricsUnavailableException();
            if (response.StatusCode == HttpStatusCode.BadRequest)
                throw new MonitoringBiometricsRejectedException("invalid_biometric_enrollment");
            throw new MonitoringBiometricsUnavailableException();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            throw new MonitoringBiometricsUnavailableException();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MonitoringBiometricsUnavailableException();
        }
    }

    public async Task RevokeAsync(string publicId, string reason, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("monitoring-module");
            using var response = await client.DeleteAsync(
                $"api/v1/monitoring/biometrics/enrollments/{Uri.EscapeDataString(publicId)}" +
                $"?reason={Uri.EscapeDataString(reason)}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NoContent) return;
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new KeyNotFoundException("Cadastro biométrico não encontrado.");
            if (response.StatusCode == HttpStatusCode.BadRequest)
                throw new MonitoringBiometricsRejectedException("invalid_biometric_enrollment");
            throw new MonitoringBiometricsUnavailableException();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            throw new MonitoringBiometricsUnavailableException();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MonitoringBiometricsUnavailableException();
        }
    }

}
