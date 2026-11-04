using System.Net;
using System.Net.Http.Json;
using GarageWatch.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class PlatformSettingsApiTests
{
    private static async Task<HttpClient> CreateAdministratorClientAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = "contract-user",
            password = "Contract-Password-123!",
        });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        return client;
    }

    [Fact]
    public async Task Administrator_reads_migrated_settings_and_updates_with_version_and_audit()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAdministratorClientAsync(factory);

        var current = await client.GetFromJsonAsync<PlatformSettingsDocument>(
            "/api/v1/admin/platform-settings");
        Assert.NotNull(current);
        Assert.True(current.Version >= 1);
        Assert.InRange(current.Values.ClipAnalysisQueueCapacity, 1, 64);
        Assert.InRange(current.Values.EventJournalMaxEvents, 1, 100_000);

        var values = new
        {
            eventJournalMaxEvents = 5_000,
            eventJournalMaxBytes = 2_097_152,
            clipAnalysisQueueCapacity = 4,
            clipAnalysisLeaseSeconds = 45,
            clipArchivePollSeconds = 3,
            clipArchiveMaxClips = 500,
            clipArchiveMaxBytes = 10_485_760,
            clipArchiveMaxClipBytes = 2_097_152,
            clipPlaybackCacheBytes = 134_217_728,
        };
        var response = await client.PutAsJsonAsync("/api/v1/admin/platform-settings", new
        {
            expectedVersion = current.Version,
            values,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<PlatformSettingsDocument>();
        Assert.NotNull(updated);
        Assert.Equal(current.Version + 1, updated.Version);
        Assert.Equal(5_000, updated.Values.EventJournalMaxEvents);
        Assert.Equal(4, updated.Values.ClipAnalysisQueueCapacity);
        Assert.Equal(45, updated.Values.ClipAnalysisLeaseSeconds);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.True(await db.PlatformEvents.CountAsync(entry => entry.EventType == "platform.settings.updated") >= 1);

        var reloaded = PlatformSettingsFile.LoadOrCreate(
            factory.Services.GetRequiredService<IConfiguration>());
        Assert.Equal(updated.Version, reloaded.Version);
        Assert.Equal(5_000, reloaded.Values.EventJournalMaxEvents);
    }

    [Fact]
    public async Task Invalid_values_are_rejected_before_persisting()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAdministratorClientAsync(factory);
        var current = await client.GetFromJsonAsync<PlatformSettingsDocument>(
            "/api/v1/admin/platform-settings");
        Assert.NotNull(current);

        var response = await client.PutAsJsonAsync("/api/v1/admin/platform-settings", new
        {
            expectedVersion = current.Version,
            values = new
            {
                eventJournalMaxEvents = 10_000,
                eventJournalMaxBytes = 67_108_864,
                clipAnalysisQueueCapacity = 0,
                clipAnalysisLeaseSeconds = 60,
                clipArchivePollSeconds = 2,
                clipArchiveMaxClips = 10_000,
                clipArchiveMaxBytes = 53_687_091_200,
                clipArchiveMaxClipBytes = 1_073_741_824,
                clipPlaybackCacheBytes = 2_147_483_648,
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Stale_version_is_rejected_with_conflict()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAdministratorClientAsync(factory);
        var current = await client.GetFromJsonAsync<PlatformSettingsDocument>(
            "/api/v1/admin/platform-settings");
        Assert.NotNull(current);

        var response = await client.PutAsJsonAsync("/api/v1/admin/platform-settings", new
        {
            expectedVersion = current.Version - 1,
            values = current.Values,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
