using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GarageWatch.Application.Identity;
using GarageWatch.Application.Portal;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Integrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class IntegrationCredentialTests
{
    private const string Secret = "aw-token-secret-123";
    private const string ActivityWatchPath = "/api/v1/admin/integrations/activitywatch/credential";

    [Fact]
    public void Protector_round_trips_and_binds_ciphertext_to_integration()
    {
        var protector = Protector("v1", "active-key-material", null, null);

        var sealedCredential = protector.Seal(Secret, IntegrationCredentialIds.ActivityWatch);

        Assert.True(protector.IsConfigured);
        Assert.Equal("v1", sealedCredential.KeyVersion);
        Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(sealedCredential.Ciphertext));
        Assert.Equal(Secret, protector.TryOpen(sealedCredential.Ciphertext, sealedCredential.Nonce,
            sealedCredential.Tag, "v1", IntegrationCredentialIds.ActivityWatch));
        Assert.Null(protector.TryOpen(sealedCredential.Ciphertext, sealedCredential.Nonce,
            sealedCredential.Tag, "v1", "another-integration"));
        Assert.Null(protector.TryOpen(sealedCredential.Ciphertext, sealedCredential.Nonce,
            sealedCredential.Tag, "v9", IntegrationCredentialIds.ActivityWatch));
    }

    [Fact]
    public void Protector_keeps_previous_key_for_rotation_and_prefers_active_key()
    {
        var original = Protector("v1", "first-key-material", null, null);
        var sealedCredential = original.Seal(Secret, IntegrationCredentialIds.ActivityWatch);

        var rotating = Protector("v2", "second-key-material", "v1", "first-key-material");

        Assert.Equal(Secret, rotating.TryOpen(sealedCredential.Ciphertext, sealedCredential.Nonce,
            sealedCredential.Tag, "v1", IntegrationCredentialIds.ActivityWatch));
        Assert.Equal("v2", rotating.ActiveKeyVersion);
        var resealed = rotating.Seal(Secret, IntegrationCredentialIds.ActivityWatch);
        Assert.Equal("v2", resealed.KeyVersion);
        Assert.Null(original.TryOpen(resealed.Ciphertext, resealed.Nonce, resealed.Tag, "v2",
            IntegrationCredentialIds.ActivityWatch));
    }

    [Fact]
    public void Protector_without_key_fails_closed()
    {
        var protector = Protector(null, null, null, null);

        Assert.False(protector.IsConfigured);
        Assert.Throws<IntegrationCredentialEncryptionUnavailableException>(
            () => protector.Seal(Secret, IntegrationCredentialIds.ActivityWatch));
    }

    [Fact]
    public async Task Admin_stores_encrypted_credential_without_echoing_the_secret()
    {
        await using var factory = new ApiFactory();
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!", sameOrigin: true);

        using var response = await admin.PutAsJsonAsync(ActivityWatchPath, new { secret = Secret });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
        using (var json = JsonDocument.Parse(body))
        {
            Assert.True(json.RootElement.GetProperty("configured").GetBoolean());
            Assert.Equal("database", json.RootElement.GetProperty("source").GetString());
            Assert.Equal("v1", json.RootElement.GetProperty("keyVersion").GetString());
        }

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var stored = await database.IntegrationCredentials.SingleAsync();
        Assert.Equal("activitywatch", stored.IntegrationId);
        Assert.Equal("v1", stored.KeyVersion);
        Assert.NotEmpty(stored.Ciphertext);
        Assert.False(stored.Ciphertext.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(Secret)));
        var administratorId = await GetUserIdAsync(factory, "contract-user");
        Assert.Equal(administratorId, stored.UpdatedByUserId);
        Assert.Equal(1, await database.IamAuditEvents.CountAsync(entry =>
            entry.Action == "integration_credential.updated" && entry.ActorUserId == administratorId));

        using var status = await admin.GetAsync(ActivityWatchPath);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.DoesNotContain(Secret, await status.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Viewer_and_cross_origin_writes_are_forbidden_and_audited_only_for_allowed_changes()
    {
        await using var factory = new ApiFactory();
        using var viewer = await LoginAsync(factory, await CreateViewerAsync(factory), "Viewer-Password-123!",
            sameOrigin: true);

        using var denied = await viewer.PutAsJsonAsync(ActivityWatchPath, new { secret = Secret });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!", sameOrigin: false);
        using var crossOrigin = await admin.PutAsJsonAsync(ActivityWatchPath, new { secret = Secret });
        Assert.Equal(HttpStatusCode.Forbidden, crossOrigin.StatusCode);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.Equal(0, await database.IntegrationCredentials.CountAsync());
        Assert.Equal(0, await database.IamAuditEvents.CountAsync(entry =>
            entry.Action.StartsWith("integration_credential")));
    }

    [Fact]
    public async Task Invalid_secret_and_unknown_integration_are_rejected()
    {
        await using var factory = new ApiFactory();
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!", sameOrigin: true);

        using var blank = await admin.PutAsJsonAsync(ActivityWatchPath, new { secret = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        using var unknown = await admin.PutAsJsonAsync("/api/v1/admin/integrations/dns/credential",
            new { secret = Secret });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Delete_removes_stored_credential_and_reports_no_effective_source()
    {
        await using var factory = new ApiFactory();
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!", sameOrigin: true);
        using var stored = await admin.PutAsJsonAsync(ActivityWatchPath, new { secret = Secret });
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);

        using var removed = await admin.DeleteAsync(ActivityWatchPath);

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        using var json = JsonDocument.Parse(await removed.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("configured").GetBoolean());
        Assert.Equal("none", json.RootElement.GetProperty("source").GetString());

        var administratorId = await GetUserIdAsync(factory, "contract-user");
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.Equal(0, await database.IntegrationCredentials.CountAsync());
        Assert.Equal(1, await database.IamAuditEvents.CountAsync(entry =>
            entry.Action == "integration_credential.removed" && entry.ActorUserId == administratorId));
    }

    [Fact]
    public async Task Store_rotates_legacy_rows_to_the_active_key_on_read()
    {
        await using var factory = new ApiFactory();
        var administratorId = await GetUserIdAsync(factory, "contract-user");
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IIamUnitOfWork>();
        var auditEvents = scope.ServiceProvider.GetRequiredService<IIamAuditEventRepository>();

        var first = new AesGcmIntegrationCredentialProtector(Configuration("v1", "first-key-material", null, null),
            NullLogger<AesGcmIntegrationCredentialProtector>.Instance);
        var v1Store = new IntegrationCredentialStore(database, unitOfWork, auditEvents, first,
            TimeProvider.System, NullLogger<IntegrationCredentialStore>.Instance);
        await v1Store.SetAsync(administratorId, IntegrationCredentialIds.ActivityWatch, Secret);

        var second = new AesGcmIntegrationCredentialProtector(
            Configuration("v2", "second-key-material", "v1", "first-key-material"),
            NullLogger<AesGcmIntegrationCredentialProtector>.Instance);
        var v2Store = new IntegrationCredentialStore(database, unitOfWork, auditEvents, second,
            TimeProvider.System, NullLogger<IntegrationCredentialStore>.Instance);

        var resolved = await v2Store.ResolveAsync(IntegrationCredentialIds.ActivityWatch);

        Assert.NotNull(resolved);
        Assert.Equal(Secret, resolved!.Value);
        Assert.Equal("v2", resolved.KeyVersion);
        var stored = await database.IntegrationCredentials.AsNoTracking().SingleAsync();
        Assert.Equal("v2", stored.KeyVersion);
        Assert.Equal(1, await database.IamAuditEvents.CountAsync(entry =>
            entry.Action == "integration_credential.rotated"));
        Assert.Null(first.TryOpen(stored.Ciphertext, stored.Nonce, stored.Tag, "v2",
            IntegrationCredentialIds.ActivityWatch));
    }

    [Fact]
    public async Task Activity_watch_client_prefers_the_stored_credential_over_the_environment_token()
    {
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        string? authorization = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new Dictionary<string, object>())
            });
        });
        var client = new ActivityWatchClient(new StubHttpClientFactory(handler),
            new ActivityWatchOptions("http://activitywatch.test", "environment-token", 3, 24),
            new FixedTimeProvider(now), new StubCredentialStore("stored-token"));

        var summary = await client.GetSummaryAsync();

        Assert.True(summary.Reachable);
        Assert.Equal("Bearer stored-token", authorization);
    }

    [Fact]
    public async Task Activity_watch_client_falls_back_to_environment_and_reports_unreadable_credentials()
    {
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        string? authorization = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new Dictionary<string, object>())
            });
        });
        var fallback = new ActivityWatchClient(new StubHttpClientFactory(handler),
            new ActivityWatchOptions("http://activitywatch.test", "environment-token", 3, 24),
            new FixedTimeProvider(now), new NullIntegrationCredentialStore());
        Assert.True((await fallback.GetSummaryAsync()).Reachable);
        Assert.Equal("Bearer environment-token", authorization);

        var unreadable = new ActivityWatchClient(new StubHttpClientFactory(handler),
            new ActivityWatchOptions("http://activitywatch.test", "environment-token", 3, 24),
            new FixedTimeProvider(now), new UnreadableCredentialStore());
        var summary = await unreadable.GetSummaryAsync();
        Assert.False(summary.Reachable);
        Assert.Equal("activitywatch_credential_unreadable", summary.ErrorCode);
    }

    private static AesGcmIntegrationCredentialProtector Protector(string? version, string? key,
        string? previousVersion, string? previousKey) =>
        new(Configuration(version, key, previousVersion, previousKey),
            NullLogger<AesGcmIntegrationCredentialProtector>.Instance);

    private static IConfiguration Configuration(string? version, string? key, string? previousVersion,
        string? previousKey)
    {
        var values = new Dictionary<string, string?>();
        if (version is not null) values[AesGcmIntegrationCredentialProtector.KeyVersionSetting] = version;
        if (key is not null)
            values[AesGcmIntegrationCredentialProtector.KeySetting] =
                Convert.ToBase64String(Encoding.UTF8.GetBytes(key.PadRight(32, '0')[..32]));
        if (previousVersion is not null)
            values[AesGcmIntegrationCredentialProtector.PreviousKeyVersionSetting] = previousVersion;
        if (previousKey is not null)
            values[AesGcmIntegrationCredentialProtector.PreviousKeySetting] =
                Convert.ToBase64String(Encoding.UTF8.GetBytes(previousKey.PadRight(32, '0')[..32]));
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async Task<string> CreateViewerAsync(ApiFactory factory)
    {
        const string username = "credential-viewer";
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = username, IsActive = true };
        Assert.True((await users.CreateAsync(user, "Viewer-Password-123!")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, "Viewer")).Succeeded);
        return username;
    }

    private static async Task<Guid> GetUserIdAsync(ApiFactory factory, string username)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await users.FindByNameAsync(username))!.Id;
    }

    private static async Task<HttpClient> LoginAsync(ApiFactory factory, string username, string password,
        bool sameOrigin)
    {
        var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        if (sameOrigin) client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        return client;
    }
}

internal sealed class StubCredentialStore(string token) : IIntegrationCredentialStore
{
    public Task<ResolvedIntegrationCredential?> ResolveAsync(string integrationId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ResolvedIntegrationCredential?>(new ResolvedIntegrationCredential(token, "v1"));

    public Task<IntegrationCredentialStatus> GetStatusAsync(string integrationId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IntegrationCredentialStatus> SetAsync(Guid actorUserId, string integrationId, string secret,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IntegrationCredentialStatus> RemoveAsync(Guid actorUserId, string integrationId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class UnreadableCredentialStore : IIntegrationCredentialStore
{
    public Task<ResolvedIntegrationCredential?> ResolveAsync(string integrationId,
        CancellationToken cancellationToken = default) =>
        throw new IntegrationCredentialUnreadableException(integrationId);

    public Task<IntegrationCredentialStatus> GetStatusAsync(string integrationId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IntegrationCredentialStatus> SetAsync(Guid actorUserId, string integrationId, string secret,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IntegrationCredentialStatus> RemoveAsync(Guid actorUserId, string integrationId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
