using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GarageWatch.Application.Analysis;
using GarageWatch.Application.Biometrics;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class RemoteBiometricTests
{
    private const string Password = "Biometric-Password-123!";
    private static readonly string TestKey = Convert.ToBase64String(
        Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());

    [Fact]
    public async Task Remote_enrollment_keeps_ciphertext_in_the_service_and_audit_in_the_portal()
    {
        var remote = new FakeBiometricClient();
        await using var factory = ConfiguredFactory(remote);
        using var owner = factory.CreateClient();
        await RegisterAsync(owner, "remote-bio-owner");
        await LoginAsync(owner, "remote-bio-owner");

        using var created = await owner.PostAsJsonAsync("/api/v1/biometrics/enrollments", EnrollmentBody());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var publicId = body.GetProperty("publicId").GetString()!;
        Assert.Matches("^BIO[0-9]{12}$", publicId);
        Assert.Equal("active", body.GetProperty("status").GetString());
        var stored = remote.Payloads[publicId];
        Assert.Equal(512, stored.EmbeddingCiphertext.Length);
        Assert.Equal(12, stored.EmbeddingNonce.Length);
        Assert.Equal(16, stored.EmbeddingTag.Length);

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            Assert.False(await database.BiometricEnrollments.AnyAsync());
            var actions = await database.IamAuditEvents.Select(item => item.Action).ToArrayAsync();
            Assert.Contains("biometric.enrollment_created", actions);
        }

        using var replacement = await owner.PostAsJsonAsync("/api/v1/biometrics/enrollments", EnrollmentBody());
        Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var revoked = await database.IamAuditEvents
                .Where(item => item.Action == "biometric.enrollment_revoked")
                .Select(item => item.ResourceId)
                .ToArrayAsync();
            Assert.Contains(publicId, revoked);
        }

        using var listed = await owner.GetAsync("/api/v1/biometrics/enrollments");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var list = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert.Equal(2, list.RootElement.GetArrayLength());

        var activeId = list.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("status").GetString() == "active")
            .GetProperty("publicId").GetString()!;
        using var revokedResponse = await owner.DeleteAsync($"/api/v1/biometrics/enrollments/{activeId}");
        Assert.Equal(HttpStatusCode.NoContent, revokedResponse.StatusCode);
        Assert.Equal("revoked", remote.Enrollments[activeId].Status);
        Assert.Equal("owner_request", remote.Enrollments[activeId].RevokedReason);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            Assert.Contains(await database.IamAuditEvents.ToArrayAsync(),
                item => item.Action == "biometric.enrollment_revoked" && item.ResourceId == activeId);
        }
    }

    [Fact]
    public async Task Remote_unavailability_returns_503_without_local_persistence_or_audit()
    {
        var remote = new FakeBiometricClient { Unavailable = true };
        await using var factory = ConfiguredFactory(remote);
        using var client = factory.CreateClient();
        await RegisterAsync(client, "remote-bio-unavailable");
        await LoginAsync(client, "remote-bio-unavailable");

        using var created = await client.PostAsJsonAsync("/api/v1/biometrics/enrollments", EnrollmentBody());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, created.StatusCode);
        Assert.Contains("biometrics_store_unavailable", await created.Content.ReadAsStringAsync());
        using var listed = await client.GetAsync("/api/v1/biometrics/enrollments");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, listed.StatusCode);
        using var revoked = await client.DeleteAsync("/api/v1/biometrics/enrollments/BIO000000000001");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, revoked.StatusCode);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.False(await database.BiometricEnrollments.AnyAsync());
        Assert.False(await database.IamAuditEvents.AnyAsync(item => item.Action.StartsWith("biometric.")));
    }

    [Fact]
    public async Task Manager_can_manage_another_account_but_unrelated_account_is_forbidden()
    {
        var remote = new FakeBiometricClient();
        await using var factory = ConfiguredFactory(remote);
        using var owner = factory.CreateClient();
        await RegisterAsync(owner, "remote-bio-target");
        await LoginAsync(owner, "remote-bio-target");
        using var other = factory.CreateClient();
        await RegisterAsync(other, "remote-bio-other");
        await LoginAsync(other, "remote-bio-other");
        var ownerPublicId = await GetUserPublicIdAsync(factory, "remote-bio-target");

        using var admin = factory.CreateClient();
        await LoginAsync(admin, "contract-user", "Contract-Password-123!");
        using var created = await admin.PostAsJsonAsync("/api/v1/biometrics/enrollments",
            EnrollmentBody() with { UserPublicId = ownerPublicId });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var enrollmentId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("publicId").GetString()!;

        using var denied = await other.DeleteAsync($"/api/v1/biometrics/enrollments/{enrollmentId}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("active", remote.Enrollments[enrollmentId].Status);

        using var revoked = await admin.DeleteAsync($"/api/v1/biometrics/enrollments/{enrollmentId}");
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal("administrator_request", remote.Enrollments[enrollmentId].RevokedReason);
    }

    [Fact]
    public async Task Client_maps_biometric_transport_failures_and_not_found()
    {
        var handler = new RecordingHandler();
        var client = new MonitoringBiometricClient(new StubFactory(handler));

        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(Array.Empty<MonitoringBiometricEnrollment>())
        };
        var list = await client.ListAsync("USR000000000001");
        Assert.Empty(list);
        Assert.Equal("/api/v1/monitoring/biometrics/enrollments?userPublicId=USR000000000001",
            handler.LastPath);

        handler.Response = new HttpResponseMessage(HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.GetAsync("BIO000000000000"));

        handler.Response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        await Assert.ThrowsAsync<MonitoringBiometricsRejectedException>(() =>
            client.EnrollAsync(RequestFor("USR000000000001")));

        var failing = new MonitoringBiometricClient(new StubFactory(new FailingHandler()));
        await Assert.ThrowsAsync<MonitoringBiometricsUnavailableException>(() =>
            failing.ListAsync("USR000000000001"));
    }

    private static WebApplicationFactory<Program> ConfiguredFactory(IMonitoringBiometricClient client) =>
        new ApiFactory(monitoringBiometricsSource: "remote", monitoringBiometricClient: client)
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["BIOMETRIC_ENCRYPTION_KEY"] = TestKey,
                        ["BIOMETRIC_ENCRYPTION_KEY_VERSION"] = "test-1"
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IFaceEnrollmentExtractor>();
                    services.AddSingleton<IFaceEnrollmentExtractor>(new TestEnrollmentExtractor());
                });
            });

    private sealed record EnrollmentRequest(string? UserPublicId, string MediaType, int Width, int Height,
        string ImageBase64, string? ConsentVersion = BiometricPolicy.ConsentVersion);

    private static EnrollmentRequest EnrollmentBody() => new(null, "image/jpeg", 160, 160,
        Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic-face-image")));

    private static MonitoringBiometricEnrollRequest RequestFor(string userPublicId) => new(userPublicId,
        new byte[128], new byte[12], new byte[16], "test-1", 128, "test-face-model", "contract-v1", "1.0",
        "test-face-adapter", BiometricPolicy.ConsentVersion);

    private static async Task RegisterAsync(HttpClient client, string username)
    {
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var registered = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = Password, confirmPassword = Password });
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
    }

    private static async Task LoginAsync(HttpClient client, string username, string? password = null)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username, password = password ?? Password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
    }

    private static async Task<string> GetUserPublicIdAsync(WebApplicationFactory<Program> factory,
        string username)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByNameAsync(username);
        Assert.NotNull(user);
        return user.PublicId!;
    }

    private sealed class TestEnrollmentExtractor : IFaceEnrollmentExtractor
    {
        public string ModelId => "test-face-model";
        public string ModelVersion => "contract-v1";
        public string AdapterId => "test-face-adapter";

        public ValueTask<FaceEmbeddingV1> ExtractAsync(FaceCropV1 crop, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FaceEmbeddingV1(FaceEmbeddingV1.CurrentSchemaVersion, ModelId, ModelVersion,
                128, Enumerable.Repeat(0.1f, 128).ToArray()));
    }

    private sealed class FakeBiometricClient : IMonitoringBiometricClient
    {
        public bool Unavailable { get; init; }
        public ConcurrentDictionary<string, MonitoringBiometricEnrollment> Enrollments { get; } = new();
        public ConcurrentDictionary<string, MonitoringBiometricEnrollRequest> Payloads { get; } = new();

        public Task<IReadOnlyList<MonitoringBiometricEnrollment>> ListAsync(string userPublicId,
            CancellationToken cancellationToken = default)
        {
            EnsureAvailable();
            return Task.FromResult<IReadOnlyList<MonitoringBiometricEnrollment>>(Enrollments.Values
                .Where(item => item.UserPublicId == userPublicId)
                .OrderByDescending(item => item.EnrolledAtUtc).ToArray());
        }

        public Task<MonitoringBiometricEnrollment> GetAsync(string publicId,
            CancellationToken cancellationToken = default)
        {
            EnsureAvailable();
            return Enrollments.TryGetValue(publicId, out var item)
                ? Task.FromResult(item)
                : throw new KeyNotFoundException();
        }

        public Task<MonitoringBiometricEnrollmentResult> EnrollAsync(
            MonitoringBiometricEnrollRequest request, CancellationToken cancellationToken = default)
        {
            EnsureAvailable();
            var now = DateTimeOffset.UtcNow;
            var replaced = Enrollments.Values
                .Where(item => item.UserPublicId == request.UserPublicId && item.Status == "active")
                .Select(item => item.PublicId).ToArray();
            foreach (var id in replaced)
                Enrollments[id] = Enrollments[id] with
                {
                    Status = "revoked",
                    RevokedAtUtc = now,
                    RevokedReason = "replaced"
                };
            var publicId = $"BIO{Random.Shared.Next(0, 1_000_000):D6}{Random.Shared.Next(0, 1_000_000):D6}";
            var created = new MonitoringBiometricEnrollment(publicId, request.UserPublicId, "active", now,
                now.AddDays(365), null, string.Empty, request.ModelId, request.ModelVersion, request.Dimensions,
                request.KeyVersion, request.ConsentVersion, now);
            Enrollments[publicId] = created;
            Payloads[publicId] = request;
            return Task.FromResult(new MonitoringBiometricEnrollmentResult(created, replaced));
        }

        public Task RevokeAsync(string publicId, string reason, CancellationToken cancellationToken = default)
        {
            EnsureAvailable();
            if (!Enrollments.TryGetValue(publicId, out var item)) throw new KeyNotFoundException();
            if (item.Status == "active")
                Enrollments[publicId] = item with
                {
                    Status = "revoked",
                    RevokedAtUtc = DateTimeOffset.UtcNow,
                    RevokedReason = reason
                };
            return Task.CompletedTask;
        }

        private void EnsureAvailable()
        {
            if (Unavailable) throw new MonitoringBiometricsUnavailableException();
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.PathAndQuery;
            return Task.FromResult(Response);
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => throw new HttpRequestException("unreachable");
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://monitoring.invalid/") };
    }
}
