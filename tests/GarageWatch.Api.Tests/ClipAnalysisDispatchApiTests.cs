using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class ClipAnalysisDispatchApiTests
{
    [Fact]
    public async Task Clip_trigger_configuration_is_per_camera_persisted_versioned_and_applies_immediately()
    {
        await using var factory = new ApiFactory();
        var internalCameraId = factory.Services.GetRequiredService<CaptureOptions>().CameraId;
        var publicCameraId = await GetCameraPublicIdAsync(factory);
        using (var anonymous = factory.CreateClient())
        {
            using var denied = await anonymous.GetAsync($"/api/v1/admin/cameras/{publicCameraId}/clip-trigger");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "trigger-viewer", IsActive = true };
            Assert.True((await users.CreateAsync(user, "Trigger-Viewer-Password-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, "User")).Succeeded);
        }
        using (var nonAdmin = await LoginAsync(factory, "trigger-viewer", "Trigger-Viewer-Password-123!"))
        {
            nonAdmin.DefaultRequestHeaders.Add("Origin", "http://localhost");
            using var denied = await nonAdmin.PutAsJsonAsync($"/api/v1/admin/cameras/{publicCameraId}/clip-trigger",
                new { expectedVersion = 0, enabled = true });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        using (var missingOrigin = await LoginAsync(factory, "contract-user", "Contract-Password-123!"))
        {
            using var denied = await missingOrigin.PutAsJsonAsync($"/api/v1/admin/cameras/{publicCameraId}/clip-trigger",
                new { expectedVersion = 0, enabled = true });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            missingOrigin.DefaultRequestHeaders.Add("Origin", "https://attacker.invalid");
            using var crossOrigin = await missingOrigin.PutAsJsonAsync($"/api/v1/admin/cameras/{publicCameraId}/clip-trigger",
                new { expectedVersion = 0, enabled = true });
            Assert.Equal(HttpStatusCode.Forbidden, crossOrigin.StatusCode);
        }
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var initial = await admin.GetAsync($"/api/v1/admin/cameras/{publicCameraId}/clip-trigger");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var initialBody = await initial.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(initialBody.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, initialBody.GetProperty("version").GetInt64());

        using var enabled = await admin.PutAsJsonAsync($"/api/v1/admin/cameras/{publicCameraId}/clip-trigger",
            new { expectedVersion = 0, enabled = true });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var enabledBody = await enabled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(enabledBody.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, enabledBody.GetProperty("version").GetInt64());
        using (var scope = factory.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<CameraClipTriggerConfigurationService>()
                .IsEnabledAsync(internalCameraId, CancellationToken.None));

        using var stale = await admin.PutAsJsonAsync($"/api/v1/admin/cameras/{publicCameraId}/clip-trigger",
            new { expectedVersion = 0, enabled = false });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var disabled = await admin.PutAsJsonAsync($"/api/v1/admin/cameras/{publicCameraId}/clip-trigger",
            new { expectedVersion = 1, enabled = false });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        using (var scope = factory.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<CameraClipTriggerConfigurationService>()
                .IsEnabledAsync(internalCameraId, CancellationToken.None));
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            Assert.Equal(2, await database.IamAuditEvents.CountAsync(item =>
                item.Action == "clip.trigger.configuration.updated"));
        }
    }

    [Fact]
    public async Task Clip_trigger_accepts_exact_https_origin_through_tls_terminating_tunnel_without_forwarded_headers()
    {
        await using var factory = new ApiFactory();
        var cameraPublicId = await GetCameraPublicIdAsync(factory);
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");

        using var wrongHost = await SendTunnelTriggerUpdateAsync(admin, cameraPublicId,
            "https://attacker.example", "home-ia.nklas.loan");
        Assert.Equal(HttpStatusCode.Forbidden, wrongHost.StatusCode);
        using var insecureOrigin = await SendTunnelTriggerUpdateAsync(admin, cameraPublicId,
            "http://home-ia.nklas.loan", "home-ia.nklas.loan");
        Assert.Equal(HttpStatusCode.Forbidden, insecureOrigin.StatusCode);
        using var wrongPort = await SendTunnelTriggerUpdateAsync(admin, cameraPublicId,
            "https://home-ia.nklas.loan:8443", "home-ia.nklas.loan");
        Assert.Equal(HttpStatusCode.Forbidden, wrongPort.StatusCode);

        using var accepted = await SendTunnelTriggerUpdateAsync(admin, cameraPublicId,
            "https://home-ia.nklas.loan", "home-ia.nklas.loan");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, body.GetProperty("version").GetInt64());
    }

    private static async Task<HttpResponseMessage> SendTunnelTriggerUpdateAsync(HttpClient client, string cameraId,
        string origin, string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/cameras/{cameraId}/clip-trigger")
        {
            Content = JsonContent.Create(new { expectedVersion = 0, enabled = true })
        };
        request.Headers.Host = host;
        request.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(request);
    }

    private static async Task<ClipManifestV1> PersistReadyClipAsync(ApiFactory factory, string cameraId)
    {
        var capture = factory.Services.GetRequiredService<CaptureOptions>();
        var clipId = Guid.NewGuid().ToString("N");
        var mediaDirectory = Path.Combine(capture.SpoolDirectory, "clips", "media");
        var manifestDirectory = Path.Combine(capture.SpoolDirectory, "clips", "manifests");
        Directory.CreateDirectory(mediaDirectory);
        Directory.CreateDirectory(manifestDirectory);
        var mediaName = $"{clipId}.ts";
        var media = Path.Combine(mediaDirectory, mediaName);
        await File.WriteAllBytesAsync(media, [0x47, 0x01, 0x02]);
        await using var mediaStream = File.OpenRead(media);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(mediaStream)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var clip = new ClipManifestV1("1.0", clipId, cameraId, now, now, now, now, "ready", "trigger-v1",
            "visual_change:test", [], mediaName, hash, null, null, 1, now,
            PublicId: new PublicIdGenerator().Generate(PublicIdPrefixes.ClipPrefix));
        await File.WriteAllTextAsync(Path.Combine(manifestDirectory, $"{clipId}.json"),
            JsonSerializer.Serialize(clip, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return clip;
    }

    private static async Task<Guid> GetVersionIdAsync(IServiceProvider services, string publicId) =>
        await services.GetRequiredService<IamDbContext>().AccessProfileVersions
            .Where(item => item.PublicId == publicId).Select(item => item.Id).SingleAsync();

    private static async Task<string> GetCameraPublicIdAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var internalId = factory.Services.GetRequiredService<CaptureOptions>().CameraId;
        return (await scope.ServiceProvider.GetRequiredService<IamDbContext>().CameraCatalog
            .Where(item => item.CameraId == internalId).Select(item => item.PublicId).SingleAsync())!;
    }

    private static async Task<HttpClient> LoginAsync(ApiFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        return client;
    }
}
