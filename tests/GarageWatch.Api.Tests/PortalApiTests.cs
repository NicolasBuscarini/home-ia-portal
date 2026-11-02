using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class PortalApiTests
{
    [Fact]
    public async Task Seeded_navigation_is_filtered_by_authorization_and_residence()
    {
        await using var factory = new ApiFactory();
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        using var adminResponse = await admin.GetAsync("/api/v1/portal/navigation");
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
        using var adminJson = JsonDocument.Parse(await adminResponse.Content.ReadAsStringAsync());
        Assert.Equal("home", adminJson.RootElement.GetProperty("residenceId").GetString());
        var adminModules = adminJson.RootElement.GetProperty("modules").EnumerateArray()
            .Select(item => item.GetProperty("moduleId").GetString()).ToArray();
        Assert.Contains("monitoring", adminModules);
        Assert.Contains("administration", adminModules);

        var viewerId = await CreateUserAsync(factory, "portal-viewer", "Viewer");
        var administratorId = await GetUserIdAsync(factory, "contract-user");
        var camera = await GetCameraAsync(factory);
        await AccessProfileTestHelper.AssignVideoAsync(factory.Services, administratorId, viewerId,
            camera.ContextId, camera.InternalCameraId);

        using var viewer = await LoginAsync(factory, "portal-viewer", "Viewer-Password-123!");
        using var viewerResponse = await viewer.GetAsync("/api/v1/portal/navigation");
        Assert.Equal(HttpStatusCode.OK, viewerResponse.StatusCode);
        using var viewerJson = JsonDocument.Parse(await viewerResponse.Content.ReadAsStringAsync());
        var viewerModules = viewerJson.RootElement.GetProperty("modules").EnumerateArray()
            .Select(item => item.GetProperty("moduleId").GetString()).ToArray();
        Assert.Single(viewerModules);
        Assert.Equal("monitoring", viewerModules[0]);

        await CreateUserAsync(factory, "portal-user", "User");
        using var user = await LoginAsync(factory, "portal-user", "Viewer-Password-123!");
        using var userResponse = await user.GetAsync("/api/v1/portal/navigation");
        using var userJson = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync());
        Assert.Empty(userJson.RootElement.GetProperty("modules").EnumerateArray());
    }

    [Fact]
    public async Task Admin_module_catalog_is_versioned_audited_and_rejects_invalid_or_cross_origin_writes()
    {
        await using var factory = new ApiFactory();
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var created = await admin.PostAsJsonAsync("/api/v1/admin/portal-modules", ModuleBody("activitywatch"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal("activitywatch", createdJson.RootElement.GetProperty("moduleId").GetString());
        Assert.Equal(1, createdJson.RootElement.GetProperty("version").GetInt64());

        using var duplicate = await admin.PostAsJsonAsync("/api/v1/admin/portal-modules", ModuleBody("activitywatch"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var stale = await admin.PutAsJsonAsync("/api/v1/admin/portal-modules/activitywatch",
            UpdateModuleBody("activitywatch", 99, "activitywatch.name"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var updated = await admin.PutAsJsonAsync("/api/v1/admin/portal-modules/activitywatch",
            UpdateModuleBody("activitywatch", 1, "activitywatch.name"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var updatedJson = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        Assert.Equal(2, updatedJson.RootElement.GetProperty("version").GetInt64());
        Assert.Equal("activitywatch.name", updatedJson.RootElement.GetProperty("displayNameKey").GetString());

        using var invalid = await admin.PostAsJsonAsync("/api/v1/admin/portal-modules", ModuleBody("Bad Id"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var unknownPermission = await admin.PostAsJsonAsync("/api/v1/admin/portal-modules",
            ModuleBody("unknown-permission", "does.not.exist"));
        Assert.Equal(HttpStatusCode.BadRequest, unknownPermission.StatusCode);

        using var noOrigin = factory.CreateClient();
        await AuthenticateAsync(noOrigin, "contract-user", "Contract-Password-123!");
        using var denied = await noOrigin.PostAsJsonAsync("/api/v1/admin/portal-modules",
            ModuleBody("no-origin"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.Equal(1, await database.PortalModules.CountAsync(module => module.ModuleId == "activitywatch"));
        Assert.Equal(2, await database.IamAuditEvents.CountAsync(entry => entry.Action.StartsWith("portal_module.")));
    }

    [Fact]
    public async Task Preferences_default_and_persist_with_validation_and_same_origin()
    {
        await using var factory = new ApiFactory();
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");

        using var defaults = await admin.GetAsync("/api/v1/portal/preferences");
        Assert.Equal(HttpStatusCode.OK, defaults.StatusCode);
        using var defaultsJson = JsonDocument.Parse(await defaults.Content.ReadAsStringAsync());
        Assert.True(defaultsJson.RootElement.GetProperty("isDefault").GetBoolean());
        Assert.Equal("forest", defaultsJson.RootElement.GetProperty("themeId").GetString());
        Assert.Contains("pt-BR", defaultsJson.RootElement.GetProperty("supportedLanguages")
            .EnumerateArray().Select(item => item.GetString()));

        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var updated = await admin.PutAsJsonAsync("/api/v1/portal/preferences",
            new { themeId = "light", languageTag = "en-US" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        using var persisted = await admin.GetAsync("/api/v1/portal/preferences");
        using var persistedJson = JsonDocument.Parse(await persisted.Content.ReadAsStringAsync());
        Assert.False(persistedJson.RootElement.GetProperty("isDefault").GetBoolean());
        Assert.Equal("light", persistedJson.RootElement.GetProperty("themeId").GetString());
        Assert.Equal("en-US", persistedJson.RootElement.GetProperty("languageTag").GetString());

        using var invalidTheme = await admin.PutAsJsonAsync("/api/v1/portal/preferences",
            new { themeId = "neon", languageTag = "pt-BR" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidTheme.StatusCode);
        using var invalidLanguage = await admin.PutAsJsonAsync("/api/v1/portal/preferences",
            new { themeId = "forest", languageTag = "xx-YY" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidLanguage.StatusCode);

        admin.DefaultRequestHeaders.Remove("Origin");
        using var noOrigin = await admin.PutAsJsonAsync("/api/v1/portal/preferences",
            new { themeId = "forest", languageTag = "pt-BR" });
        Assert.Equal(HttpStatusCode.Forbidden, noOrigin.StatusCode);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.Equal(1, await database.IamAuditEvents.CountAsync(entry =>
            entry.Action == "portal.preferences.updated"));
    }

    [Fact]
    public async Task Authorization_decisions_return_only_scoped_permissions_and_reject_invalid_input()
    {
        await using var factory = new ApiFactory();
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        var camera = await GetCameraAsync(factory);

        using var adminDecision = await admin.PostAsJsonAsync("/api/v1/portal/authorization/decisions",
            DecisionBody("monitoring", "home", ["live.video.view"],
                [new { contextId = camera.ContextPublicId, cameraId = camera.PublicId }]));
        Assert.Equal(HttpStatusCode.OK, adminDecision.StatusCode);
        using var adminJson = JsonDocument.Parse(await adminDecision.Content.ReadAsStringAsync());
        Assert.True(adminJson.RootElement.GetProperty("allowed").GetBoolean());
        Assert.True(adminJson.RootElement.GetProperty("permissions")[0].GetProperty("allowed").GetBoolean());

        using var adminOnly = await admin.PostAsJsonAsync("/api/v1/portal/authorization/decisions",
            DecisionBody("monitoring", "home", ["admin.users.manage"], []));
        using var adminOnlyJson = JsonDocument.Parse(await adminOnly.Content.ReadAsStringAsync());
        Assert.True(adminOnlyJson.RootElement.GetProperty("allowed").GetBoolean());

        var viewerId = await CreateUserAsync(factory, "portal-decision-viewer", "Viewer");
        await AccessProfileTestHelper.AssignVideoAsync(factory.Services,
            await GetUserIdAsync(factory, "contract-user"), viewerId, camera.ContextId, camera.InternalCameraId);
        using var viewer = await LoginAsync(factory, "portal-decision-viewer", "Viewer-Password-123!");
        using var viewerDecision = await viewer.PostAsJsonAsync("/api/v1/portal/authorization/decisions",
            DecisionBody("monitoring", "home", ["live.video.view"],
                [new { contextId = camera.ContextPublicId, cameraId = camera.PublicId }]));
        using var viewerJson = JsonDocument.Parse(await viewerDecision.Content.ReadAsStringAsync());
        Assert.True(viewerJson.RootElement.GetProperty("allowed").GetBoolean());

        using var viewerAdminOnly = await viewer.PostAsJsonAsync("/api/v1/portal/authorization/decisions",
            DecisionBody("monitoring", "home", ["admin.users.manage"], []));
        using var viewerAdminOnlyJson = JsonDocument.Parse(await viewerAdminOnly.Content.ReadAsStringAsync());
        Assert.False(viewerAdminOnlyJson.RootElement.GetProperty("allowed").GetBoolean());

        using var unknownPermission = await viewer.PostAsJsonAsync("/api/v1/portal/authorization/decisions",
            DecisionBody("monitoring", "home", ["does.not.exist"], []));
        Assert.Equal(HttpStatusCode.BadRequest, unknownPermission.StatusCode);
        using var wrongResidence = await viewer.PostAsJsonAsync("/api/v1/portal/authorization/decisions",
            DecisionBody("monitoring", "other", ["live.video.view"], []));
        Assert.Equal(HttpStatusCode.BadRequest, wrongResidence.StatusCode);
        using var unknownAudience = await viewer.PostAsJsonAsync("/api/v1/portal/authorization/decisions",
            DecisionBody("unknown", "home", ["live.video.view"], []));
        Assert.Equal(HttpStatusCode.BadRequest, unknownAudience.StatusCode);
    }

    private static async Task<HttpClient> LoginAsync(ApiFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        await AuthenticateAsync(client, username, password);
        return client;
    }

    private static async Task AuthenticateAsync(HttpClient client, string username, string password)
    {
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
    }

    private static async Task<Guid> CreateUserAsync(ApiFactory factory, string username, string role)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = username, IsActive = true };
        Assert.True((await users.CreateAsync(user, "Viewer-Password-123!")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user.Id;
    }

    private static async Task<Guid> GetUserIdAsync(ApiFactory factory, string username)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await users.FindByNameAsync(username))!.Id;
    }

    private static async Task<CameraFixture> GetCameraAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var capture = scope.ServiceProvider.GetRequiredService<CaptureOptions>();
        var camera = await database.CameraCatalog.AsNoTracking()
            .Select(item => new
            {
                item.PublicId,
                ContextPublicId = item.Context.PublicId,
                item.ContextId,
                item.CameraId
            }).SingleAsync(item => item.CameraId == capture.CameraId);
        return new CameraFixture(camera.PublicId!, camera.ContextPublicId!, camera.ContextId, camera.CameraId);
    }

    private static object ModuleBody(string moduleId, string? requiredPermission = "clips.read") => new
    {
        moduleId,
        displayNameKey = $"{moduleId}.name",
        contractVersion = "1.0",
        apiBaseUrl = "http://127.0.0.1:5174",
        healthPath = "/health/ready",
        requiredPermission,
        menuGroup = "integrations",
        menuOrder = 20,
        menuIcon = "module",
        menuLabelKey = $"{moduleId}.menu",
        isEnabled = true
    };

    private static object UpdateModuleBody(string moduleId, long version, string displayNameKey) => new
    {
        version,
        moduleId,
        displayNameKey,
        contractVersion = "1.0",
        apiBaseUrl = "http://127.0.0.1:5174",
        healthPath = "/health/ready",
        requiredPermission = "clips.read",
        menuGroup = "integrations",
        menuOrder = 20,
        menuIcon = "module",
        menuLabelKey = $"{moduleId}.menu",
        isEnabled = true
    };

    private static object DecisionBody(string audience, string residenceId, string[] permissionKeys,
        object[] scopes) => new { audience, residenceId, permissionKeys, scopes };

    private sealed record CameraFixture(string PublicId, string ContextPublicId, string ContextId,
        string InternalCameraId);
}
