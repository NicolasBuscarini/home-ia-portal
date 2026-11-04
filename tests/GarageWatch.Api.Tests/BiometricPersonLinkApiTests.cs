using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GarageWatch.Application.Analysis;
using GarageWatch.Application.Biometrics;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Identity;
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

public sealed class BiometricPersonLinkApiTests
{
    private const string Password = "Biometric-Password-123!";
    private static readonly string TestKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());

    [Fact]
    public async Task Admin_links_enrollment_to_roster_person_and_list_projects_it()
    {
        var biometrics = new FakeBiometricClient();
        var roster = new FakeRosterClient();
        await using var factory = ConfiguredFactory(biometrics, roster);
        using var admin = factory.CreateClient();
        await LoginAsync(admin, "contract-user", "Contract-Password-123!");

        var ownerPublicId = await RegisterUserAsync(factory, "link-owner");
        var enrollmentId = await EnrollAsync(admin, ownerPublicId);
        var firstPerson = await CreatePersonAsync(admin, "Pessoa Um");
        var secondPerson = await CreatePersonAsync(admin, "Pessoa Dois");

        using var linked = await admin.PutAsJsonAsync(
            $"/api/v1/admin/biometrics/enrollments/{enrollmentId}/person",
            new { personPublicId = firstPerson });
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        var link = await linked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(enrollmentId, link.GetProperty("enrollmentPublicId").GetString());
        Assert.Equal(firstPerson, link.GetProperty("personPublicId").GetString());

        using var listed = await admin.GetAsync($"/api/v1/biometrics/enrollments?userPublicId={ownerPublicId}");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var item = Assert.Single(await listed.Content.ReadFromJsonAsync<JsonElement[]>() ?? []);
        Assert.Equal(firstPerson, item.GetProperty("personPublicId").GetString());

        using var relinked = await admin.PutAsJsonAsync(
            $"/api/v1/admin/biometrics/enrollments/{enrollmentId}/person",
            new { personPublicId = secondPerson });
        Assert.Equal(HttpStatusCode.OK, relinked.StatusCode);
        using var afterRelink = await admin.GetAsync($"/api/v1/biometrics/enrollments?userPublicId={ownerPublicId}");
        var relinkedItem = Assert.Single(await afterRelink.Content.ReadFromJsonAsync<JsonElement[]>() ?? []);
        Assert.Equal(secondPerson, relinkedItem.GetProperty("personPublicId").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var rows = await database.BiometricPersonLinks
                .Where(link => link.EnrollmentPublicId == enrollmentId).ToArrayAsync();
            Assert.Equal(2, rows.Length);
            Assert.Equal(secondPerson, Assert.Single(rows, row => row.UnlinkedAtUtc == null).KnownPersonPublicId);
            var actions = await database.PlatformEvents
                .Where(entry => entry.ResourceId == enrollmentId).Select(entry => entry.EventType).ToArrayAsync();
            Assert.Contains("biometric.person_linked", actions);
            Assert.Contains("biometric.person_unlinked", actions);
        }

        using var unlinked = await admin.DeleteAsync(
            $"/api/v1/admin/biometrics/enrollments/{enrollmentId}/person");
        Assert.Equal(HttpStatusCode.NoContent, unlinked.StatusCode);
        using var afterUnlink = await admin.GetAsync($"/api/v1/biometrics/enrollments?userPublicId={ownerPublicId}");
        var unlinkedItem = Assert.Single(await afterUnlink.Content.ReadFromJsonAsync<JsonElement[]>() ?? []);
        Assert.Equal(JsonValueKind.Null, unlinkedItem.GetProperty("personPublicId").ValueKind);
    }

    [Fact]
    public async Task Non_admin_cannot_link_or_unlink()
    {
        var biometrics = new FakeBiometricClient();
        var roster = new FakeRosterClient();
        await using var factory = ConfiguredFactory(biometrics, roster);
        using var owner = factory.CreateClient();
        await RegisterAsync(owner, "link-owner");
        await LoginAsync(owner, "link-owner");
        var ownerPublicId = await GetUserPublicIdAsync(factory, "link-owner");
        var enrollmentId = await EnrollAsync(owner, ownerPublicId);

        using var admin = factory.CreateClient();
        await LoginAsync(admin, "contract-user", "Contract-Password-123!");
        var personId = await CreatePersonAsync(admin, "Pessoa Autorizada");

        using var deniedLink = await owner.PutAsJsonAsync(
            $"/api/v1/admin/biometrics/enrollments/{enrollmentId}/person",
            new { personPublicId = personId });
        Assert.Equal(HttpStatusCode.Forbidden, deniedLink.StatusCode);
        using var deniedUnlink = await owner.DeleteAsync(
            $"/api/v1/admin/biometrics/enrollments/{enrollmentId}/person");
        Assert.Equal(HttpStatusCode.Forbidden, deniedUnlink.StatusCode);
    }

    [Fact]
    public async Task Unknown_or_inactive_targets_are_rejected_with_sanitized_codes()
    {
        var biometrics = new FakeBiometricClient();
        var roster = new FakeRosterClient();
        await using var factory = ConfiguredFactory(biometrics, roster);
        using var admin = factory.CreateClient();
        await LoginAsync(admin, "contract-user", "Contract-Password-123!");
        var ownerPublicId = await RegisterUserAsync(factory, "link-owner");
        var enrollmentId = await EnrollAsync(admin, ownerPublicId);
        var personId = await CreatePersonAsync(admin, "Pessoa Um");

        using var unknownEnrollment = await admin.PutAsJsonAsync(
            "/api/v1/admin/biometrics/enrollments/BIO999999999999/person",
            new { personPublicId = personId });
        Assert.Equal(HttpStatusCode.NotFound, unknownEnrollment.StatusCode);

        using var unknownPerson = await admin.PutAsJsonAsync(
            $"/api/v1/admin/biometrics/enrollments/{enrollmentId}/person",
            new { personPublicId = "PER999999999999" });
        Assert.Equal(HttpStatusCode.NotFound, unknownPerson.StatusCode);

        using var invalidPerson = await admin.PutAsJsonAsync(
            $"/api/v1/admin/biometrics/enrollments/{enrollmentId}/person",
            new { personPublicId = "not-a-person" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidPerson.StatusCode);

        using var revoke = await admin.DeleteAsync($"/api/v1/biometrics/enrollments/{enrollmentId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using var inactive = await admin.PutAsJsonAsync(
            $"/api/v1/admin/biometrics/enrollments/{enrollmentId}/person",
            new { personPublicId = personId });
        Assert.Equal(HttpStatusCode.BadRequest, inactive.StatusCode);
    }

    [Fact]
    public async Task Revoking_enrollment_or_removing_person_unlinks_active_association()
    {
        var biometrics = new FakeBiometricClient();
        var roster = new FakeRosterClient();
        await using var factory = ConfiguredFactory(biometrics, roster);
        using var admin = factory.CreateClient();
        await LoginAsync(admin, "contract-user", "Contract-Password-123!");
        var ownerPublicId = await RegisterUserAsync(factory, "link-owner");
        var enrollmentId = await EnrollAsync(admin, ownerPublicId);
        var personId = await CreatePersonAsync(admin, "Pessoa Um");

        using var linked = await admin.PutAsJsonAsync(
            $"/api/v1/admin/biometrics/enrollments/{enrollmentId}/person",
            new { personPublicId = personId });
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);

        using var revoke = await admin.DeleteAsync($"/api/v1/biometrics/enrollments/{enrollmentId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var row = await database.BiometricPersonLinks
                .SingleAsync(link => link.EnrollmentPublicId == enrollmentId);
            Assert.NotNull(row.UnlinkedAtUtc);
        }

        var secondEnrollment = await EnrollAsync(admin, ownerPublicId);
        using var secondLinked = await admin.PutAsJsonAsync(
            $"/api/v1/admin/biometrics/enrollments/{secondEnrollment}/person",
            new { personPublicId = personId });
        Assert.Equal(HttpStatusCode.OK, secondLinked.StatusCode);

        using var removed = await admin.DeleteAsync($"/api/v1/admin/known-people/{personId}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var row = await database.BiometricPersonLinks
                .SingleAsync(link => link.EnrollmentPublicId == secondEnrollment);
            Assert.NotNull(row.UnlinkedAtUtc);
        }
    }

    [Fact]
    public async Task Roster_unavailable_returns_service_unavailable_without_persisting_link()
    {
        var biometrics = new FakeBiometricClient();
        var roster = new FakeRosterClient();
        await using var factory = ConfiguredFactory(biometrics, roster);
        using var admin = factory.CreateClient();
        await LoginAsync(admin, "contract-user", "Contract-Password-123!");
        var ownerPublicId = await RegisterUserAsync(factory, "link-owner");
        var enrollmentId = await EnrollAsync(admin, ownerPublicId);
        roster.Unavailable = true;

        using var response = await admin.PutAsJsonAsync(
            $"/api/v1/admin/biometrics/enrollments/{enrollmentId}/person",
            new { personPublicId = "PER999999999999" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("monitoring_roster_unavailable", await response.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.False(await database.BiometricPersonLinks.AnyAsync());
    }

    private static WebApplicationFactory<Program> ConfiguredFactory(FakeBiometricClient biometrics,
        FakeRosterClient roster) =>
        new ApiFactory(monitoringBiometricClient: biometrics, monitoringRosterClient: roster)
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

    private static async Task<string> RegisterUserAsync(WebApplicationFactory<Program> factory, string username)
    {
        using var client = factory.CreateClient();
        await RegisterAsync(client, username);
        return await GetUserPublicIdAsync(factory, username);
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

    private static async Task<string> EnrollAsync(HttpClient client, string? userPublicId)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/biometrics/enrollments",
            new EnrollmentRequest(userPublicId, "image/jpeg", 160, 160,
                Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic-face-image"))));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("publicId").GetString()!;
    }

    private static async Task<string> CreatePersonAsync(HttpClient client, string displayName)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/admin/known-people",
            new { displayName, purpose = "Teste de vínculo biométrico", authorizationConfirmed = true });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("publicId").GetString()!;
    }

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

    private sealed record EnrollmentRequest(string? UserPublicId, string MediaType, int Width, int Height,
        string ImageBase64, string? ConsentVersion = BiometricPolicy.ConsentVersion);

    private sealed class TestEnrollmentExtractor : IFaceEnrollmentExtractor
    {
        public string ModelId => "test-face-model";
        public string ModelVersion => "contract-v1";
        public string AdapterId => "test-face-adapter";

        public ValueTask<FaceEmbeddingV1> ExtractAsync(FaceCropV1 crop, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!crop.IsValid) throw new ArgumentException("Recorte inválido.", nameof(crop));
            return ValueTask.FromResult(new FaceEmbeddingV1(FaceEmbeddingV1.CurrentSchemaVersion,
                ModelId, ModelVersion, 3, [0.1f, 0.2f, 0.3f]));
        }
    }

    private sealed class FakeRosterClient : IMonitoringRosterClient
    {
        private int _sequence;
        public bool Unavailable { get; set; }
        public Dictionary<string, MonitoringRosterPerson> People { get; } = new(StringComparer.Ordinal);

        public Task<MonitoringRosterPage> ListAsync(int page, int pageSize,
            CancellationToken cancellationToken = default)
        {
            if (Unavailable) throw new MonitoringRosterUnavailableException();
            var all = People.Values.Where(person => person.DisplayName is not null)
                .OrderBy(person => person.DisplayName, StringComparer.Ordinal).ToArray();
            return Task.FromResult(new MonitoringRosterPage(all.Skip((page - 1) * pageSize).Take(pageSize).ToArray(),
                page, pageSize, all.Length));
        }

        public Task<MonitoringRosterPerson> CreateAsync(MonitoringRosterCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Unavailable) throw new MonitoringRosterUnavailableException();
            var now = DateTimeOffset.UtcNow;
            var publicId = $"PER{Interlocked.Increment(ref _sequence):D12}";
            var person = new MonitoringRosterPerson(publicId, request.DisplayName, request.Purpose, now, now, 1);
            People[publicId] = person;
            return Task.FromResult(person);
        }

        public Task RemoveAsync(string publicId, CancellationToken cancellationToken = default)
        {
            if (Unavailable) throw new MonitoringRosterUnavailableException();
            if (!People.TryGetValue(publicId, out var person)) throw new KeyNotFoundException();
            People[publicId] = person with { DisplayName = null!, Purpose = null!, Version = person.Version + 1 };
            return Task.CompletedTask;
        }
    }
}
