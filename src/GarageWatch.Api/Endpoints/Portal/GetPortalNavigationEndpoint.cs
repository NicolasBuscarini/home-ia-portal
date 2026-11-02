using GarageWatch.Application.Portal;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Portal;

public sealed class GetPortalNavigationEndpoint() : BaseEndpoint("Portal", "GetPortalNavigation",
    "Get the authorized portal navigation",
    "Returns enabled modules and menu metadata filtered by the current account's permissions. The response is a projection; the server remains the authorization authority.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/portal/navigation", async (ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, IPortalNavigation navigation, CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            return Results.Ok(await navigation.GetNavigationAsync(user.Id, cancellationToken));
        }).RequireAuthorization("Viewer")
            .Produces<PortalNavigation>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
