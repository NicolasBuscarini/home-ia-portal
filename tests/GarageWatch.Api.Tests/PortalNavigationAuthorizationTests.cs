using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Application.Authorization;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class PortalNavigationAuthorizationTests
{
    [Fact]
    public async Task Global_scope_grants_expose_global_module_permissions_in_navigation()
    {
        await using var factory = new ApiFactory();
        var adminId = await AdministratorIdAsync(factory);
        var viewerId = await CreateViewerAsync(factory, "global-scope-viewer");

        await GrantAsync(factory, adminId, viewerId, "Global biométrica",
            ["biometrics.manage", "activitywatch.read"], [PermissionScope.Global]);

        var moduleIds = await NavigationModuleIdsAsync(factory, "global-scope-viewer");

        Assert.Contains("biometrics", moduleIds);
        Assert.Contains("activitywatch", moduleIds);
        Assert.DoesNotContain("administration", moduleIds);
    }

    [Fact]
    public async Task Module_scope_grants_expose_their_owning_module_in_navigation()
    {
        await using var factory = new ApiFactory();
        var adminId = await AdministratorIdAsync(factory);
        var viewerId = await CreateViewerAsync(factory, "module-scope-viewer");

        await GrantAsync(factory, adminId, viewerId, "Biometria do módulo",
            ["biometrics.manage"], [PermissionScope.ForModule("biometrics")]);

        var moduleIds = await NavigationModuleIdsAsync(factory, "module-scope-viewer");

        Assert.Contains("biometrics", moduleIds);
        Assert.DoesNotContain("administration", moduleIds);
    }

    [Fact]
    public async Task Camera_scope_grants_still_expose_camera_scoped_monitoring()
    {
        await using var factory = new ApiFactory();
        var adminId = await AdministratorIdAsync(factory);
        var viewerId = await CreateViewerAsync(factory, "camera-scope-viewer");
        string cameraId;
        using (var scope = factory.Services.CreateScope())
            cameraId = scope.ServiceProvider.GetRequiredService<CaptureOptions>().CameraId;
        await AccessProfileTestHelper.AssignVideoAsync(factory.Services, adminId, viewerId, "garage", cameraId);

        var moduleIds = await NavigationModuleIdsAsync(factory, "camera-scope-viewer");

        Assert.Contains("monitoring", moduleIds);
        Assert.DoesNotContain("biometrics", moduleIds);
    }

    private static async Task<Guid> AdministratorIdAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByNameAsync("contract-user");
        return user!.Id;
    }

    private static async Task<Guid> CreateViewerAsync(ApiFactory factory, string username)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var viewer = new ApplicationUser { Id = Guid.NewGuid(), UserName = username, IsActive = true };
        Assert.True((await users.CreateAsync(viewer, "Viewer-Password-123!")).Succeeded);
        Assert.True((await users.AddToRoleAsync(viewer, "Viewer")).Succeeded);
        return viewer.Id;
    }

    private static async Task GrantAsync(ApiFactory factory, Guid adminId, Guid userId, string name,
        string[] permissionKeys, PermissionScope[] scopes)
    {
        using var scope = factory.Services.CreateScope();
        var profiles = scope.ServiceProvider.GetRequiredService<AccessProfileService>();
        var created = await profiles.CreateAsync(adminId, name, permissionKeys, CancellationToken.None);
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var versionId = await database.AccessProfileVersions.AsNoTracking()
            .Where(version => version.PublicId == created.CurrentVersionPublicId)
            .Select(version => version.Id).SingleAsync();
        await profiles.ReplaceAssignmentsAsync(adminId, userId,
            [new ProfileAssignmentRequest(versionId, scopes)], CancellationToken.None);
    }

    private static async Task<string[]> NavigationModuleIdsAsync(ApiFactory factory, string username)
    {
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username, password = "Viewer-Password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        using var response = await client.GetAsync("/api/v1/portal/navigation");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("modules").EnumerateArray()
            .Select(module => module.GetProperty("moduleId").GetString()!).ToArray();
    }
}
