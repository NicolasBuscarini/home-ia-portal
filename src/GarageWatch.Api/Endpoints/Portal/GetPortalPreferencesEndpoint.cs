using GarageWatch.Application.Portal;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Portal;

public sealed class GetPortalPreferencesEndpoint() : BaseEndpoint("Portal", "GetPortalPreferences",
    "Get portal preferences",
    "Returns the account's theme/language preference, or safe defaults when no preference is stored, plus the supported values.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/portal/preferences", async (ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, IPortalPreferences preferences, CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            return Results.Ok(await preferences.GetPreferencesAsync(user.Id, cancellationToken));
        }).RequireAuthorization("Viewer")
            .Produces<PortalPreferences>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
