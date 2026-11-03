using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Application.Authorization;
using GarageWatch.Application.Portal;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Integrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class ActivityWatchModuleTests
{
    private static readonly string[] ForbiddenFragments = ["Secret Bank", "https://", "token-value"];

    [Fact]
    public async Task Aggregates_currentwindow_events_and_drops_titles_urls_and_afk()
    {
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        var requests = new List<(HttpMethod Method, string Path, string? Authorization)>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString()));
            if (request.RequestUri!.AbsolutePath.EndsWith("/buckets/", StringComparison.Ordinal))
                return Json(new Dictionary<string, object>
                {
                    ["aw-watcher-window_pc"] = new { type = "currentwindow" },
                    ["aw-watcher-afk_pc"] = new { type = "afkstatus" }
                });
            return Json(new object[]
            {
                new
                {
                    timestamp = "2026-11-03T11:00:00+00:00", duration = 60,
                    data = new { app = "Firefox", title = "Secret Bank", url = "https://bank.invalid" }
                },
                new { timestamp = "2026-11-03T11:05:00+00:00", duration = 30, data = new { app = "Firefox" } },
                new { timestamp = "2026-11-03T11:10:00+00:00", duration = 45, data = new { app = "Code" } }
            });
        });
        var client = new ActivityWatchClient(new StubHttpClientFactory(handler),
            new ActivityWatchOptions("http://activitywatch.test", "token-value", 3, 24), new FixedTimeProvider(now),
            new NullIntegrationCredentialStore());

        var summary = await client.GetSummaryAsync();

        Assert.True(summary.Reachable);
        Assert.Null(summary.ErrorCode);
        Assert.Equal(now.AddHours(-24), summary.WindowStartUtc);
        Assert.Equal(135, summary.TotalActiveSeconds);
        Assert.Collection(summary.Applications,
            first => Assert.Equal(("Firefox", 90d), (first.App, first.Seconds)),
            second => Assert.Equal(("Code", 45d), (second.App, second.Seconds)));
        Assert.All(requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.All(requests, request => Assert.Equal("Bearer token-value", request.Authorization));
        var json = JsonSerializer.Serialize(summary);
        Assert.All(ForbiddenFragments, fragment => Assert.DoesNotContain(fragment, json));
        Assert.DoesNotContain(requests, request => request.Authorization?.Contains("secret", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task Clamps_events_to_the_window_and_marks_truncation()
    {
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        var events = Enumerable.Range(0, 12)
            .Select(index => (object)new
            {
                timestamp = "2026-11-03T11:00:00+00:00",
                duration = 10,
                data = new { app = $"App {index:00}" }
            })
            .Append(new
            {
                timestamp = "2026-11-03T10:00:00+00:00",
                duration = 100_000,
                data = new { app = "Clamped" }
            })
            .Append(new
            {
                timestamp = "2026-10-01T11:00:00+00:00",
                duration = 10,
                data = new { app = "Ancient" }
            })
            .ToArray();
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/buckets/", StringComparison.Ordinal)
                ? Json(new Dictionary<string, object> { ["window"] = new { type = "currentwindow" } })
                : Json(events));
        var client = new ActivityWatchClient(new StubHttpClientFactory(handler),
            new ActivityWatchOptions("http://activitywatch.test", null, 3, 24), new FixedTimeProvider(now),
            new NullIntegrationCredentialStore());

        var summary = await client.GetSummaryAsync();

        Assert.True(summary.Reachable);
        Assert.True(summary.Truncated);
        Assert.Equal(10, summary.Applications.Count);
        Assert.Equal(120 + 7200, summary.TotalActiveSeconds);
        Assert.Contains(summary.Applications, item => item.App == "Clamped" && item.Seconds == 7200);
        Assert.DoesNotContain(summary.Applications, item => item.App == "Ancient");
    }

    [Fact]
    public async Task Timeout_error_and_malformed_payload_are_isolated_with_sanitized_codes()
    {
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        var options = new ActivityWatchOptions("http://activitywatch.test", "token-value", 1, 24);

        var timeout = new ActivityWatchClient(
            new StubHttpClientFactory(new StubHttpMessageHandler(_ => throw new TaskCanceledException())),
            options, new FixedTimeProvider(now), new NullIntegrationCredentialStore());
        Assert.Equal("activitywatch_timeout", (await timeout.GetSummaryAsync()).ErrorCode);

        var failing = new ActivityWatchClient(
            new StubHttpClientFactory(new StubHttpMessageHandler(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)))),
            options, new FixedTimeProvider(now), new NullIntegrationCredentialStore());
        var failed = await failing.GetSummaryAsync();
        Assert.False(failed.Reachable);
        Assert.Equal("activitywatch_error", failed.ErrorCode);
        Assert.Empty(failed.Applications);

        var malformed = new ActivityWatchClient(
            new StubHttpClientFactory(new StubHttpMessageHandler(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"not\":")
                }))),
            options, new FixedTimeProvider(now), new NullIntegrationCredentialStore());
        Assert.Equal("activitywatch_invalid_response", (await malformed.GetSummaryAsync()).ErrorCode);
    }

    [Fact]
    public async Task Summary_endpoint_requires_permission_and_audits_admin_reads()
    {
        var module = new StubActivityWatchModule(ReachableSummary());
        await using var factory = new ApiFactory(activityWatchModule: module);
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");

        using var response = await admin.GetAsync("/api/v1/portal/modules/activitywatch/summary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.GetProperty("reachable").GetBoolean());
        Assert.Equal("activitywatch", json.RootElement.GetProperty("moduleId").GetString());
        Assert.DoesNotContain("token-value", body, StringComparison.Ordinal);

        var viewerId = await CreateViewerAsync(factory, "activity-viewer");
        using var viewer = await LoginAsync(factory, "activity-viewer", "Viewer-Password-123!");
        using var denied = await viewer.GetAsync("/api/v1/portal/modules/activitywatch/summary");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(1, module.Calls);

        var administratorId = await GetUserIdAsync(factory, "contract-user");
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.Equal(1, await database.IamAuditEvents.CountAsync(entry =>
            entry.Action == "activitywatch.read" && entry.ActorUserId == administratorId));
        Assert.Equal(1, await database.IamAuditEvents.CountAsync(entry =>
            entry.Action == "activitywatch.read.denied" && entry.ActorUserId == viewerId));
    }

    [Fact]
    public async Task Unavailable_upstream_returns_sanitized_503_without_leaking_configuration()
    {
        var module = new StubActivityWatchModule(new ActivityWatchSummary("1.0", "activitywatch", false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(-24), DateTimeOffset.UtcNow, 0, false, [],
            "activitywatch_unreachable"));
        await using var factory = new ApiFactory(activityWatchModule: module);
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");

        using var response = await admin.GetAsync("/api/v1/portal/modules/activitywatch/summary");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal("activitywatch_unreachable", json.RootElement.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("activitywatch.test", body, StringComparison.Ordinal);
        Assert.DoesNotContain("activitywatch.invalid", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_module_is_not_served_and_not_navigable()
    {
        await using var factory = new ApiFactory(activityWatchModule: new StubActivityWatchModule(ReachableSummary()));
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var entity = await database.PortalModules.SingleAsync(module => module.ModuleId == "activitywatch");
            entity.IsEnabled = false;
            await database.SaveChangesAsync();
        }
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");

        using var response = await admin.GetAsync("/api/v1/portal/modules/activitywatch/summary");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var navigation = await admin.GetAsync("/api/v1/portal/navigation");
        using var json = JsonDocument.Parse(await navigation.Content.ReadAsStringAsync());
        var moduleIds = json.RootElement.GetProperty("modules").EnumerateArray()
            .Select(item => item.GetProperty("moduleId").GetString()).ToArray();
        Assert.DoesNotContain("activitywatch", moduleIds);
    }

    private static ActivityWatchSummary ReachableSummary() => new("1.0", "activitywatch", true,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(-24), DateTimeOffset.UtcNow, 90, false,
        [new ActivityWatchApplicationUsage("Firefox", 90, 2)], null);

    private static async Task<Guid> CreateViewerAsync(ApiFactory factory, string username)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = username, IsActive = true };
        Assert.True((await users.CreateAsync(user, "Viewer-Password-123!")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, "Viewer")).Succeeded);
        return user.Id;
    }

    private static async Task<Guid> GetUserIdAsync(ApiFactory factory, string username)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await users.FindByNameAsync(username))!.Id;
    }

    private static async Task<HttpClient> LoginAsync(ApiFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        return client;
    }

    private static Task<HttpResponseMessage> Json(object payload) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken) => responder(request);
}

internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
    {
        BaseAddress = new Uri("http://activitywatch.test/")
    };
}

internal sealed class StubActivityWatchModule(ActivityWatchSummary summary) : IActivityWatchModule
{
    public int Calls { get; private set; }

    public Task<ActivityWatchSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(summary);
    }
}

internal sealed class NullIntegrationCredentialStore : GarageWatch.Application.Portal.IIntegrationCredentialStore
{
    public Task<GarageWatch.Application.Portal.ResolvedIntegrationCredential?> ResolveAsync(string integrationId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<GarageWatch.Application.Portal.ResolvedIntegrationCredential?>(null);

    public Task<GarageWatch.Application.Portal.IntegrationCredentialStatus> GetStatusAsync(string integrationId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<GarageWatch.Application.Portal.IntegrationCredentialStatus> SetAsync(Guid actorUserId,
        string integrationId, string secret, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<GarageWatch.Application.Portal.IntegrationCredentialStatus> RemoveAsync(Guid actorUserId,
        string integrationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
