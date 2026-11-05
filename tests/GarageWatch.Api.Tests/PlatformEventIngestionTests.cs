using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GarageWatch.Api.Endpoints.Events;
using GarageWatch.Application.PlatformEvents;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class PlatformEventIngestionTests
{
    private const string ServiceSecret = "test-monitoring-service-secret-0123456789abcdef";
    private const string Password = "Contract-Password-123!";

    [Fact]
    public async Task Anonymous_ingestion_is_rejected()
    {
        using var factory = new ApiFactory();
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync("/api/v1/events", BrowserEvent());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Session_ingests_navigation_event_with_server_derived_context()
    {
        using var factory = new ApiFactory();
        using var client = await LoggedInClient(factory);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TestAgent/1.0");
        var correlation = Guid.NewGuid().ToString("D");
        client.DefaultRequestHeaders.Add("X-Correlation-ID", correlation);

        using var response = await client.PostAsJsonAsync("/api/v1/events",
            BrowserEvent(requestId: "forged", correlationId: "forged"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(correlation, response.Headers.GetValues("X-Correlation-ID").Single());
        var created = await response.Content.ReadFromJsonAsync<PlatformEventCreatedResponse>();
        Assert.NotNull(created);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await users.FindByNameAsync("contract-user");
        var row = await database.PlatformEvents.AsNoTracking()
            .SingleAsync(entry => entry.PublicId == created!.PublicId);
        Assert.Equal(admin!.Id, row.ActorUserId);
        Assert.Equal("navigation.viewed", row.EventType);
        Assert.Equal("shell", row.Source);
        Assert.Equal("browser", row.Emitter);
        Assert.Equal(correlation, row.CorrelationId);
        Assert.NotEqual("forged", row.RequestId);
        Assert.Equal(32, row.SessionId?.Length);
        Assert.Equal("TestAgent/1.0", row.ClientUserAgent);
        Assert.Equal(64, row.PayloadHash?.Length);
    }

    [Fact]
    public async Task Session_cannot_forge_actor_emitter_or_non_ui_type()
    {
        using var factory = new ApiFactory();
        using var client = await LoggedInClient(factory);

        using var forgedActor = await client.PostAsJsonAsync("/api/v1/events",
            BrowserEvent(actor: new { userPublicId = "USR000000000042" }));
        Assert.Equal(HttpStatusCode.Forbidden, forgedActor.StatusCode);

        using var forgedEmitter = await client.PostAsJsonAsync("/api/v1/events",
            BrowserEvent(emitter: "server"));
        Assert.Equal(HttpStatusCode.Forbidden, forgedEmitter.StatusCode);

        using var serviceType = await client.PostAsJsonAsync("/api/v1/events",
            BrowserEvent(eventType: "clip.preserved", category: "media"));
        Assert.Equal(HttpStatusCode.Forbidden, serviceType.StatusCode);
    }

    [Fact]
    public async Task Service_token_ingests_event_for_its_own_source()
    {
        using var factory = new ApiFactory();
        using var client = Client(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Mint());

        using var response = await client.PostAsJsonAsync("/api/v1/events", ServiceEvent());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var created = await response.Content.ReadFromJsonAsync<PlatformEventCreatedResponse>();
        var row = await database.PlatformEvents.AsNoTracking()
            .SingleAsync(entry => entry.PublicId == created!.PublicId);
        Assert.Equal("monitoring", row.Source);
        Assert.Equal("service", row.Emitter);
        Assert.Null(row.ActorUserId);
        Assert.Equal("clip", row.ResourceType);
        Assert.Equal("CLP000000000123", row.ResourceId);
        Assert.Contains("\"policyVersion\":4", row.MetadataJson);
    }

    [Fact]
    public async Task Invalid_service_tokens_are_rejected_and_cannot_use_other_sources()
    {
        using var factory = new ApiFactory();
        using var client = Client(factory);

        foreach (var token in new[]
                 {
                     Mint(scope: "monitoring.read"),
                     Mint(audience: "monitoring"),
                     Mint(residence: "other")
                 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events")
            {
                Content = JsonContent.Create(ServiceEvent())
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Mint());
        using var wrongSource = await client.PostAsJsonAsync("/api/v1/events",
            ServiceEvent(source: "shell", emitter: "browser"));
        Assert.Equal(HttpStatusCode.Forbidden, wrongSource.StatusCode);
    }

    [Fact]
    public async Task Duplicate_event_id_is_idempotent()
    {
        using var factory = new ApiFactory();
        using var client = Client(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Mint());
        var eventId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;
        var body = ServiceEvent(eventId: eventId, occurredAt: occurredAt);

        using var first = await client.PostAsJsonAsync("/api/v1/events", body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<PlatformEventCreatedResponse>();

        using var duplicate = await client.PostAsJsonAsync("/api/v1/events", body);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        var duplicateBody = await duplicate.Content.ReadFromJsonAsync<PlatformEventCreatedResponse>();
        Assert.Equal(firstBody!.PublicId, duplicateBody!.PublicId);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.Equal(1, await database.PlatformEvents.CountAsync(entry => entry.EventId == eventId));
    }

    [Fact]
    public async Task Invalid_event_is_sanitized_and_oversized_body_is_rejected()
    {
        using var factory = new ApiFactory();
        using var client = Client(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Mint());

        using var invalid = await client.PostAsJsonAsync("/api/v1/events",
            ServiceEvent(eventType: "unknown.event"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var error = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(PlatformEventValidationErrors.UnknownEventType, error.GetProperty("code").GetString());
        Assert.DoesNotContain("payload", await invalid.Content.ReadAsStringAsync());

        using var oversized = await client.PostAsync("/api/v1/events",
            new StringContent(new string('x', 17 * 1024), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
    }

    [Fact]
    public async Task Rate_limit_returns_429_with_retry_after()
    {
        using var factory = new ApiFactory(eventIngestRateLimit: 2);
        using var client = await LoggedInClient(factory);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var accepted = await client.PostAsJsonAsync("/api/v1/events", BrowserEvent());
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }

        using var rejected = await client.PostAsJsonAsync("/api/v1/events", BrowserEvent());
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("60", rejected.Headers.GetValues("Retry-After").Single());
    }

    private static HttpClient Client(ApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private static async Task<HttpClient> LoggedInClient(ApiFactory factory)
    {
        var client = Client(factory);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = Password });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        return client;
    }

    private static object BrowserEvent(string eventType = "navigation.viewed",
        string category = "navigation", string emitter = "browser", object? actor = null,
        string? requestId = null, string? correlationId = null) => new
        {
            schemaVersion = "1.0",
            eventId = Guid.NewGuid(),
            eventType,
            category,
            severity = "info",
            outcome = "success",
            occurredAtUtc = DateTimeOffset.UtcNow,
            source = "shell",
            emitter,
            actor,
            correlationId,
            requestId,
            traceId = new string('a', 32),
            spanId = new string('b', 16),
            client = new { browser = "Firefox", os = "Linux", device = "desktop" }
        };

    private static object ServiceEvent(Guid? eventId = null, DateTimeOffset? occurredAt = null,
        string eventType = "clip.preserved", string source = "monitoring",
        string emitter = "service", string severity = "info") => new
        {
            schemaVersion = "1.0",
            eventId = eventId ?? Guid.NewGuid(),
            eventType,
            category = "media",
            severity,
            outcome = "success",
            occurredAtUtc = occurredAt ?? DateTimeOffset.UtcNow,
            source,
            sourceVersion = "1.0",
            emitter,
            resource = new { type = "clip", publicId = "CLP000000000123" },
            metadata = new { isPreserved = true, policyVersion = 4 }
        };

    private static string Mint(string subject = "monitoring", string audience = "home-ia-portal",
        string scope = "events.write", string residence = "home")
    {
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" })));
        var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iss = "monitoring",
            aud = audience,
            sub = subject,
            residence_id = residence,
            scope,
            iat = now.AddSeconds(-1).ToUnixTimeSeconds(),
            exp = now.AddMinutes(1).ToUnixTimeSeconds(),
            jti = Guid.NewGuid().ToString("N")
        })));
        var signature = Base64Url.EncodeToString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(ServiceSecret),
            Encoding.ASCII.GetBytes($"{header}.{payload}")));
        return $"{header}.{payload}.{signature}";
    }
}
