using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Application.Portal;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class PortalHealthApiTests
{
    [Fact]
    public async Task Aggregated_health_distinguishes_portal_modules_and_integration_without_leaking_details()
    {
        await using var factory = new ApiFactory(activityWatchModule: new StubActivityWatchModule(Summary(true)),
            monitoringModuleClient: new CapturingModuleClient());
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");

        using var response = await admin.GetAsync("/api/v1/portal/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal("1.0", json.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("home", json.RootElement.GetProperty("residenceId").GetString());
        Assert.Equal("healthy", json.RootElement.GetProperty("portalStatus").GetString());
        Assert.Equal("healthy", json.RootElement.GetProperty("status").GetString());

        var modules = json.RootElement.GetProperty("modules").EnumerateArray()
            .ToDictionary(item => item.GetProperty("moduleId").GetString()!);
        Assert.Equal(4, modules.Count);
        var biometrics = modules["biometrics"];
        Assert.Equal("module", biometrics.GetProperty("kind").GetString());
        Assert.Equal("healthy", biometrics.GetProperty("status").GetString());
        var monitoring = modules["monitoring"];
        Assert.Equal("module", monitoring.GetProperty("kind").GetString());
        Assert.Equal("healthy", monitoring.GetProperty("status").GetString());
        Assert.Contains("remote-capture", monitoring.GetProperty("capabilities").EnumerateArray()
            .Select(item => item.GetString()));
        var administration = modules["administration"];
        Assert.Equal("module", administration.GetProperty("kind").GetString());
        Assert.Equal("healthy", administration.GetProperty("status").GetString());
        var activityWatch = modules["activitywatch"];
        Assert.Equal("integration", activityWatch.GetProperty("kind").GetString());
        Assert.Equal("healthy", activityWatch.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, activityWatch.GetProperty("errorCode").ValueKind);

        Assert.DoesNotContain("http", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);

        await CreateUserAsync(factory, "health-viewer", "Viewer");
        using var viewer = await LoginAsync(factory, "health-viewer", "Viewer-Password-123!");
        using var denied = await viewer.GetAsync("/api/v1/portal/health");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Disabled_and_unavailable_modules_are_reported_and_change_the_aggregate_status()
    {
        await using var factory = new ApiFactory(
            activityWatchModule: new StubActivityWatchModule(Summary(false), healthy: false),
            monitoringModuleClient: new CapturingModuleClient());
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var activityWatch = await database.PortalModules.SingleAsync(module => module.ModuleId == "activitywatch");
        activityWatch.IsEnabled = false;
        await database.SaveChangesAsync();

        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        using var disabledResponse = await admin.GetAsync("/api/v1/portal/health");
        Assert.Equal(HttpStatusCode.OK, disabledResponse.StatusCode);
        using var disabledJson = JsonDocument.Parse(await disabledResponse.Content.ReadAsStringAsync());
        Assert.Equal("healthy", disabledJson.RootElement.GetProperty("status").GetString());
        var disabledEntry = Module(disabledJson, "activitywatch");
        Assert.False(disabledEntry.GetProperty("enabled").GetBoolean());
        Assert.Equal("disabled", disabledEntry.GetProperty("status").GetString());

        activityWatch.IsEnabled = true;
        await database.SaveChangesAsync();
        using var unavailableResponse = await admin.GetAsync("/api/v1/portal/health");
        Assert.Equal(HttpStatusCode.OK, unavailableResponse.StatusCode);
        using var unavailableJson = JsonDocument.Parse(await unavailableResponse.Content.ReadAsStringAsync());
        Assert.Equal("unavailable", unavailableJson.RootElement.GetProperty("status").GetString());
        var unavailableEntry = Module(unavailableJson, "activitywatch");
        Assert.Equal("unavailable", unavailableEntry.GetProperty("status").GetString());
        Assert.Equal("activitywatch_unreachable", unavailableEntry.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Module_without_a_known_probe_is_unknown_and_degrades_the_aggregate()
    {
        await using var factory = new ApiFactory(activityWatchModule: new StubActivityWatchModule(Summary(true)),
            monitoringModuleClient: new CapturingModuleClient());
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var created = await admin.PostAsJsonAsync("/api/v1/admin/portal-modules", new
        {
            moduleId = "external-lab",
            displayNameKey = "external.name",
            contractVersion = "1.0",
            apiBaseUrl = "http://127.0.0.1:5999",
            healthPath = "/health/ready",
            requiredPermission = "clips.read",
            menuGroup = "integrations",
            menuOrder = 30,
            menuIcon = "module",
            menuLabelKey = "external.menu",
            isEnabled = true
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var response = await admin.GetAsync("/api/v1/portal/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("degraded", json.RootElement.GetProperty("status").GetString());
        var external = Module(json, "external-lab");
        Assert.Equal("unknown", external.GetProperty("status").GetString());
        Assert.Equal("health_probe_not_configured", external.GetProperty("errorCode").GetString());
    }

    private static JsonElement Module(JsonDocument json, string moduleId) =>
        json.RootElement.GetProperty("modules").EnumerateArray()
            .Single(item => item.GetProperty("moduleId").GetString() == moduleId);

    private static ActivityWatchSummary Summary(bool reachable)
    {
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        return new ActivityWatchSummary("1.0", "activitywatch", reachable, now, now.AddHours(-1), now, 0, false, [],
            reachable ? null : "activitywatch_unreachable");
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

    private static async Task CreateUserAsync(ApiFactory factory, string username, string role)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = username, IsActive = true };
        Assert.True((await users.CreateAsync(user, "Viewer-Password-123!")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
    }
}
