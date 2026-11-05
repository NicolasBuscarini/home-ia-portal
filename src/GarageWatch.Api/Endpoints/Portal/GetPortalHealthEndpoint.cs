using GarageWatch.Application.Portal;

using GarageWatch.Application.Authorization;

namespace GarageWatch.Api.Endpoints.Portal;

public sealed class GetPortalHealthEndpoint() : BaseEndpoint("Portal", "GetPortalHealth",
    "Read aggregated portal and module health",
    "Returns the portal status and the health of each catalog module with sanitized error codes. Requires Administrator; no URL, secret or media is exposed.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/portal/health", async (IPortalHealth health, CancellationToken cancellationToken) =>
        {
            var report = await health.GetAsync(cancellationToken);
            return Results.Ok(report);
        }).RequireAuthorization(PermissionPolicy.Name("admin.system.health.read"))
            .Produces<PortalHealthReport>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
