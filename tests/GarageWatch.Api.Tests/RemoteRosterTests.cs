using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class RemoteRosterTests
{
    [Fact]
    public async Task Remote_roster_round_trip_keeps_the_audit_trail_in_the_portal()
    {
        var roster = new FakeRosterClient();
        await using var factory = new ApiFactory(monitoringRosterSource: "remote", monitoringRosterClient: roster);
        using var admin = await LoginAsync(factory);
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var created = await admin.PostAsJsonAsync("/api/v1/admin/known-people",
            new { displayName = "Remote Person", purpose = "Household", authorizationConfirmed = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var person = await created.Content.ReadFromJsonAsync<JsonElement>();
        var publicId = person.GetProperty("publicId").GetString()!;
        Assert.Matches("^PER[0-9]{12}$", publicId);
        Assert.Single(roster.People);
        Assert.Equal("Remote Person", roster.People[publicId].DisplayName);

        using var listed = await admin.GetAsync("/api/v1/admin/known-people");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var page = await listed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(publicId, page.GetProperty("items")[0].GetProperty("publicId").GetString());

        using var removed = await admin.DeleteAsync($"/api/v1/admin/known-people/{publicId}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Null(roster.People[publicId].DisplayName);
        using var repeated = await admin.DeleteAsync($"/api/v1/admin/known-people/{publicId}");
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);

        using var empty = await admin.GetAsync("/api/v1/admin/known-people");
        var emptyPage = await empty.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(emptyPage.GetProperty("items").EnumerateArray());

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var actions = await database.PlatformEvents.Select(item => item.EventType).ToArrayAsync();
        Assert.Contains("known_person.created", actions);
        Assert.Contains("known_person.removed", actions);
        Assert.False(await database.KnownPeople.AnyAsync(item => item.PublicId == publicId));
    }

    [Fact]
    public async Task Remote_roster_unavailability_returns_503_and_never_falls_back_to_local()
    {
        await using var factory = new ApiFactory(monitoringRosterSource: "remote",
            monitoringRosterClient: new FakeRosterClient { Unavailable = true });
        using var admin = await LoginAsync(factory);
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var listed = await admin.GetAsync("/api/v1/admin/known-people");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, listed.StatusCode);
        using var created = await admin.PostAsJsonAsync("/api/v1/admin/known-people",
            new { displayName = "Unavailable Person", purpose = "Household", authorizationConfirmed = true });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, created.StatusCode);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.False(await database.KnownPeople.AnyAsync());
        Assert.False(await database.PlatformEvents.AnyAsync(item => item.EventType.StartsWith("known_person")));
    }

    [Fact]
    public async Task Client_maps_roster_transport_success_and_not_found()
    {
        var handler = new RecordingHandler();
        var client = new MonitoringRosterClient(new StubFactory(handler));

        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new MonitoringRosterPage([], 1, 25, 0))
        };
        var page = await client.ListAsync(1, 25);
        Assert.Equal(0, page.Total);
        Assert.Equal("/api/v1/monitoring/roster/people?page=1&pageSize=25", handler.LastPath);

        handler.Response = new HttpResponseMessage(HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.RemoveAsync("PER000000000000"));
    }

    [Fact]
    public async Task Client_surfaces_transport_failures_instead_of_returning_empty_data()
    {
        var client = new MonitoringRosterClient(new StubFactory(new FailingHandler()));
        await Assert.ThrowsAsync<MonitoringRosterUnavailableException>(() => client.ListAsync(1, 25));
    }

    private static async Task<HttpClient> LoginAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "Contract-Password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        return client;
    }

    private sealed class FakeRosterClient : IMonitoringRosterClient
    {
        public bool Unavailable { get; init; }
        public ConcurrentDictionary<string, MonitoringRosterPerson> People { get; } = new();

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
            var publicId = $"PER{Random.Shared.Next(0, 1_000_000):D6}{Random.Shared.Next(0, 1_000_000):D6}";
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
