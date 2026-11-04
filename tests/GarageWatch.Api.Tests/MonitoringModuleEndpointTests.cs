using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class MonitoringModuleEndpointTests
{
    [Fact]
    public async Task Monitoring_module_health_requires_session_and_reports_the_remote_contract()
    {
        await using var factory = new ApiFactory(monitoringModuleClient: new CapturingModuleClient());
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/portal/modules/monitoring/health")).StatusCode);

        using var admin = factory.CreateClient();
        using var login = await admin.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "Contract-Password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        admin.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);

        using var response = await admin.GetAsync("/api/v1/portal/modules/monitoring/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("monitoring", json.RootElement.GetProperty("moduleId").GetString());
        Assert.Equal("remote", json.RootElement.GetProperty("executionMode").GetString());
        Assert.True(json.RootElement.GetProperty("reachable").GetBoolean());
    }
}
