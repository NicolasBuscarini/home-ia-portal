using GarageWatch.Api.Endpoints;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Portal;

public sealed class GetMonitoringModuleHealthEndpoint() : BaseEndpoint("Portal", "GetMonitoringModuleHealth",
    "Read the monitoring module contract/health",
    "Returns the in-process projection or the configured remote monitoring contract. Remote failure is reported as 503 without exposing internals.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/portal/modules/monitoring/health", async (ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, IMonitoringModuleClient monitoring, CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var contract = await monitoring.GetContractAsync(cancellationToken);
            return Results.Json(contract, statusCode: contract.Reachable
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable);
        }).RequireAuthorization("Viewer")
            .Produces<MonitoringModuleContract>(StatusCodes.Status200OK)
            .Produces<MonitoringModuleContract>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
