using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GarageWatch.Infrastructure.Monitoring;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class MonitoringServiceTokenTests
{
    private const string Secret = "test-monitoring-service-secret-0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Issued_token_carries_audience_residence_and_scope()
    {
        var issuer = new MonitoringServiceTokenIssuer(Options(Secret), new FixedTimeProvider(Now));

        var token = issuer.Issue(MonitoringServiceTokenIssuer.ReadScope);

        Assert.NotNull(token);
        var parts = token!.Split('.');
        Assert.Equal(3, parts.Length);
        var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1])).RootElement;
        Assert.Equal("home-ia-portal", payload.GetProperty("iss").GetString());
        Assert.Equal("monitoring", payload.GetProperty("aud").GetString());
        Assert.Equal("home", payload.GetProperty("residence_id").GetString());
        Assert.Equal("monitoring.read", payload.GetProperty("scope").GetString());
        Assert.Equal(Now.AddSeconds(60).ToUnixTimeSeconds(), payload.GetProperty("exp").GetInt64());

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret),
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
        Assert.Equal(expected, Base64Url.DecodeFromChars(parts[2]));
    }

    [Fact]
    public void Missing_secret_produces_no_token()
    {
        var issuer = new MonitoringServiceTokenIssuer(Options(null), new FixedTimeProvider(Now));

        Assert.Null(issuer.Issue(MonitoringServiceTokenIssuer.ReadScope));
    }

    [Fact]
    public async Task Handler_attaches_read_scope_for_get_and_write_scope_for_commands()
    {
        var issuer = new MonitoringServiceTokenIssuer(Options(Secret), new FixedTimeProvider(Now));
        var handler = new CapturingHandler();
        using var client = new HttpClient(new MonitoringServiceTokenHandler(issuer) { InnerHandler = handler })
        {
            BaseAddress = new Uri("http://monitoring.invalid/")
        };

        await client.GetAsync("api/v1/monitoring/contract");
        await client.PostAsync("api/v1/monitoring/analysis/jobs", new StringContent("{}"));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(MonitoringServiceTokenIssuer.ReadScope, Scope(handler.Requests[0].Headers.Authorization));
        Assert.Equal(MonitoringServiceTokenIssuer.WriteScope, Scope(handler.Requests[1].Headers.Authorization));
    }

    private static string? Scope(System.Net.Http.Headers.AuthenticationHeaderValue? authorization)
    {
        var token = authorization?.Parameter;
        Assert.NotNull(token);
        var parts = token!.Split('.');
        return JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1])).RootElement
            .GetProperty("scope").GetString();
    }

    private static MonitoringServiceTokenOptions Options(string? secret) =>
        new(secret, "home-ia-portal", "monitoring", "home");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
