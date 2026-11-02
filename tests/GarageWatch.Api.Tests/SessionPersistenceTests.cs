using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class SessionPersistenceTests
{
    [Fact]
    public async Task Login_issues_a_persistent_twelve_hour_cookie()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var before = DateTimeOffset.UtcNow;
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "Contract-Password-123!" });
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("garagewatch.session=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        var expires = Regex.Match(cookie, "expires=(?<value>[^;]+)", RegexOptions.IgnoreCase);
        Assert.True(expires.Success, $"O cookie deve ser persistente e trazer expires=; recebido: {cookie}");
        Assert.True(DateTimeOffset.TryParse(expires.Groups["value"].Value.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var expiration));
        Assert.InRange(expiration - before, TimeSpan.FromHours(11.9), TimeSpan.FromHours(12.1));
        Assert.InRange(expiration - after, TimeSpan.FromHours(11.9), TimeSpan.FromHours(12.1));
    }

    [Fact]
    public async Task Security_stamp_renewal_keeps_the_expiry_set_at_login()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "Contract-Password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var loginCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        var loginExpiry = Expiration(loginCookie);

        using var authenticated = factory.CreateClient(new() { AllowAutoRedirect = false });
        authenticated.DefaultRequestHeaders.Add("Cookie", loginCookie.Split(';', 2)[0]);
        await Task.Delay(TimeSpan.FromSeconds(2));
        using var me = await authenticated.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var renewed = Assert.Single(me.Headers.GetValues("Set-Cookie"));
        Assert.InRange(Expiration(renewed) - loginExpiry, TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Session_cookie_still_authenticates_after_a_host_restart_that_reuses_the_key_ring()
    {
        var directory = Path.Combine(Path.GetTempPath(), "garagewatch-tests", $"session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "garagewatch.db");
        var keyRingPath = Path.Combine(directory, "keys");

        var cookie = await LoginAsync(databasePath, keyRingPath);
        Assert.True(Directory.EnumerateFiles(keyRingPath, "*.xml").Any(), "o key ring deve ser persistido em disco");

        await using var restarted = new ApiFactory(databasePath: databasePath, dataProtectionPath: keyRingPath);
        using var client = restarted.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        using var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Session_cookie_is_rejected_when_the_key_ring_is_not_shared()
    {
        var directory = Path.Combine(Path.GetTempPath(), "garagewatch-tests", $"session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "garagewatch.db");

        var cookie = await LoginAsync(databasePath, Path.Combine(directory, "keys-original"));

        await using var restarted = new ApiFactory(databasePath: databasePath,
            dataProtectionPath: Path.Combine(directory, "keys-different"));
        using var client = restarted.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        using var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    private static DateTimeOffset Expiration(string setCookie)
    {
        var expires = Regex.Match(setCookie, "expires=(?<value>[^;]+)", RegexOptions.IgnoreCase);
        Assert.True(expires.Success, $"Cookie sem expires=: {setCookie}");
        Assert.True(DateTimeOffset.TryParse(expires.Groups["value"].Value.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var expiration));
        return expiration;
    }

    private static async Task<string> LoginAsync(string databasePath, string keyRingPath)
    {
        await using var factory = new ApiFactory(databasePath: databasePath, dataProtectionPath: keyRingPath);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "Contract-Password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';', 2)[0];
    }
}
