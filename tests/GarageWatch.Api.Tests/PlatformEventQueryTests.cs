using System.Net;
using System.Net.Http.Json;
using System.Text;
using GarageWatch.Application.PlatformEvents;
using GarageWatch.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class PlatformEventQueryTests
{
    private const string Password = "Contract-Password-123!";

    [Fact]
    public async Task Admin_list_filters_with_default_window_and_stable_cursor()
    {
        await using var factory = new ApiFactory();
        using var client = await AdministratorClient(factory);
        var now = DateTimeOffset.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            Seed(database, "EVT000000000101", "camera.connection.address.updated", now.AddHours(-1),
                correlation: "corr-00000001");
            Seed(database, "EVT000000000102", "camera.connection.address.updated", now.AddHours(-2),
                correlation: "corr-00000002");
            Seed(database, "EVT000000000103", "camera.connection.address.updated", now.AddHours(-3),
                correlation: "corr-00000003");
            Seed(database, "EVT000000000104", "camera.connection.address.updated", now.AddDays(-2),
                correlation: "corr-00000004");
            Seed(database, "EVT000000000105", "camera.access.denied", now.AddHours(-1),
                outcome: PlatformEventOutcomes.Denied, resourceId: "CLP000000000105",
                correlation: "corr-00000005");
            await database.SaveChangesAsync();
        }

        var first = await client.GetFromJsonAsync<PlatformEventPage>(
            "/api/v1/admin/events?eventType=camera.connection.address.updated&pageSize=2");
        Assert.NotNull(first);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal("EVT000000000101", first.Items[0].PublicId);
        Assert.Equal("EVT000000000102", first.Items[1].PublicId);
        Assert.NotNull(first.NextCursor);

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            Seed(database, "EVT000000000106", "camera.connection.address.updated", now.AddMinutes(-5),
                correlation: "corr-00000006");
            await database.SaveChangesAsync();
        }

        var second = await client.GetFromJsonAsync<PlatformEventPage>(
            $"/api/v1/admin/events?eventType=camera.connection.address.updated&pageSize=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.NotNull(second);
        Assert.Equal("EVT000000000103", Assert.Single(second.Items).PublicId);
        Assert.Null(second.NextCursor);

        var filtered = await client.GetFromJsonAsync<PlatformEventPage>(
            "/api/v1/admin/events?category=administration&outcome=denied&source=portal&resourceId=CLP000000000105&q=corr-00000005");
        Assert.NotNull(filtered);
        Assert.Equal("EVT000000000105", Assert.Single(filtered.Items).PublicId);

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var audit = await database.PlatformEvents.FirstAsync(entry => entry.EventType == "event_store.queried");
            Assert.Equal("administration", audit.Category);
            Assert.Contains("\"resultCount\"", audit.MetadataJson);
        }
    }

    [Fact]
    public async Task Admin_list_projects_actor_username_and_excludes_selected_actors()
    {
        await using var factory = new ApiFactory();
        using var client = await AdministratorClient(factory);
        ApplicationUser first;
        ApplicationUser second;
        string firstPublicId;
        string secondPublicId;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<
                Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
            first = new ApplicationUser { Id = Guid.NewGuid(), UserName = "audit-alpha", IsActive = true };
            second = new ApplicationUser { Id = Guid.NewGuid(), UserName = "audit-beta", IsActive = true };
            Assert.True((await users.CreateAsync(first, Password)).Succeeded);
            Assert.True((await users.CreateAsync(second, Password)).Succeeded);
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var now = DateTimeOffset.UtcNow.AddMinutes(-2);
            Seed(database, "EVT000000000301", "camera.connection.address.updated", now,
                correlation: "corr-exclusion-0001", actorUserId: first.Id);
            Seed(database, "EVT000000000302", "camera.connection.address.updated", now.AddSeconds(1),
                correlation: "corr-exclusion-0001", actorUserId: second.Id);
            Seed(database, "EVT000000000303", "camera.connection.address.updated", now.AddSeconds(2),
                correlation: "corr-exclusion-0001");
            await database.SaveChangesAsync();
            firstPublicId = await database.Users.Where(user => user.Id == first.Id)
                .Select(user => user.PublicId!).SingleAsync();
            secondPublicId = await database.Users.Where(user => user.Id == second.Id)
                .Select(user => user.PublicId!).SingleAsync();
        }

        var all = await client.GetFromJsonAsync<PlatformEventPage>(
            "/api/v1/admin/events?correlationId=corr-exclusion-0001&pageSize=10");
        Assert.NotNull(all);
        Assert.Equal(3, all.Items.Count);
        Assert.Equal("audit-alpha", all.Items.Single(item => item.PublicId == "EVT000000000301")
            .ActorUsername);
        Assert.Equal("audit-beta", all.Items.Single(item => item.PublicId == "EVT000000000302")
            .ActorUsername);

        var byActor = await client.GetFromJsonAsync<PlatformEventPage>(
            $"/api/v1/admin/events?correlationId=corr-exclusion-0001&actor={firstPublicId}&pageSize=10");
        Assert.NotNull(byActor);
        Assert.Equal("EVT000000000301", Assert.Single(byActor.Items).PublicId);

        var byUsername = await client.GetFromJsonAsync<PlatformEventPage>(
            "/api/v1/admin/events?correlationId=corr-exclusion-0001&excludeActor=audit-beta&pageSize=10");
        Assert.NotNull(byUsername);
        Assert.DoesNotContain(byUsername.Items, item => item.PublicId == "EVT000000000302");
        Assert.Equal(2, byUsername.Items.Count);

        var byPublicId = await client.GetFromJsonAsync<PlatformEventPage>(
            $"/api/v1/admin/events?correlationId=corr-exclusion-0001&excludeActor={firstPublicId}&excludeActor={secondPublicId}&pageSize=10");
        Assert.NotNull(byPublicId);
        Assert.Equal("EVT000000000303", Assert.Single(byPublicId.Items).PublicId);
    }

    [Fact]
    public async Task Query_rejects_anonymous_non_admin_and_malformed_parameters()
    {
        await using var factory = new ApiFactory();
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/admin/events")).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
            var viewer = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = "event-viewer",
                IsActive = true,
                LockoutEnabled = true
            };
            Assert.True((await users.CreateAsync(viewer, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(viewer, "Viewer")).Succeeded);
        }

        using var viewerClient = factory.CreateClient();
        viewerClient.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var viewerLogin = await viewerClient.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "event-viewer", password = Password });
        Assert.Equal(HttpStatusCode.NoContent, viewerLogin.StatusCode);
        viewerClient.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(viewerLogin.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewerClient.GetAsync("/api/v1/admin/events")).StatusCode);

        using var admin = await AdministratorClient(factory);
        foreach (var path in new[]
                 {
                     "/api/v1/admin/events?fromUtc=2026-01-02&toUtc=2026-01-01",
                     "/api/v1/admin/events?pageSize=101",
                     "/api/v1/admin/events?cursor=not-a-cursor",
                     "/api/v1/admin/events?category=unknown"
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync("/api/v1/admin/events/EVT123")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync("/api/v1/admin/events/EVT000000000999")).StatusCode);
    }

    [Fact]
    public async Task Detail_reports_integrity_and_typed_metadata_without_secrets()
    {
        await using var factory = new ApiFactory();
        using var client = await AdministratorClient(factory);
        var occurred = DateTimeOffset.UtcNow.AddHours(-1);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var metadata = new Dictionary<string, object?>
            {
                ["isPreserved"] = true,
                ["policyVersion"] = 4
            };
            Seed(database, "EVT000000000201", "clip.preserved", occurred,
                category: PlatformEventCategories.Media, resourceId: "CLP000000000201",
                metadata: metadata);
            await database.SaveChangesAsync();
        }

        var detail = await client.GetFromJsonAsync<PlatformEventDetail>(
            "/api/v1/admin/events/EVT000000000201");
        Assert.NotNull(detail);
        Assert.True(detail.IntegrityValid);
        Assert.Equal("clip", detail.Record.Event.Resource!.Type);
        Assert.Equal("CLP000000000201", detail.Record.Event.Resource.PublicId);
        Assert.True(detail.Metadata!["isPreserved"].GetBoolean());
        Assert.Equal(4, detail.Metadata["policyVersion"].GetInt32());

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var stored = await database.PlatformEvents.SingleAsync(entry => entry.PublicId == "EVT000000000201");
            stored.PayloadHash = new string('0', 64);
            await database.SaveChangesAsync();
        }

        var tampered = await client.GetFromJsonAsync<PlatformEventDetail>(
            "/api/v1/admin/events/EVT000000000201");
        Assert.NotNull(tampered);
        Assert.False(tampered.IntegrityValid);

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            Assert.True(await database.PlatformEvents.AnyAsync(entry => entry.EventType == "event_store.viewed"));
        }
    }

    private static async Task<HttpClient> AdministratorClient(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = Password });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        return client;
    }

    private static void Seed(IamDbContext database, string publicId, string eventType,
        DateTimeOffset occurredAt, string category = PlatformEventCategories.Administration,
        string outcome = PlatformEventOutcomes.Success, string? resourceId = null,
        string? correlation = null, IReadOnlyDictionary<string, object?>? metadata = null,
        Guid? actorUserId = null)
    {
        var resource = resourceId is null ? null : new PlatformEventReferenceV1("clip", resourceId);
        var value = new PlatformEventV1(Guid.NewGuid(), eventType, category,
            PlatformEventSeverities.Info, outcome, occurredAt, PlatformEventSources.Portal, null,
            PlatformEventEmitters.Server, Resource: resource, CorrelationId: correlation,
            Metadata: metadata);
        database.PlatformEvents.Add(new PlatformEvent
        {
            Id = Guid.NewGuid(),
            PublicId = publicId,
            EventId = value.EventId,
            ActorUserId = actorUserId,
            SchemaVersion = PlatformEventContract.SchemaVersion,
            EventType = eventType,
            Category = category,
            Severity = PlatformEventSeverities.Info,
            Outcome = outcome,
            Source = PlatformEventSources.Portal,
            Emitter = PlatformEventEmitters.Server,
            ResourceType = resource?.Type,
            ResourceId = resourceId,
            CorrelationId = correlation,
            MetadataJson = metadata is null ? null : Encoding.UTF8.GetString(
                PlatformEventCanonicalizer.SerializeMetadata(metadata)),
            OccurredAtUtc = occurredAt,
            RecordedAtUtc = occurredAt,
            PayloadHash = PlatformEventCanonicalizer.ComputePayloadHash(value)
        });
    }
}
