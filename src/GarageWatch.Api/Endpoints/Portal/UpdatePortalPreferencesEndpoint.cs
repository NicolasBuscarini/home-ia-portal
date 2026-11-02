using GarageWatch.Api.Contracts;
using GarageWatch.Application.Portal;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Portal;

public sealed class UpdatePortalPreferencesEndpoint() : BaseEndpoint("Portal", "UpdatePortalPreferences",
    "Update portal preferences",
    "Persists the account's theme/language preference. Requires same-origin Origin and audits the mutation.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPut("/api/v1/portal/preferences", async (UpdatePortalPreferencesRequest request,
            HttpContext context, ClaimsPrincipal principal, UserManager<ApplicationUser> users,
            IPortalPreferences preferences, CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            try
            {
                return Results.Ok(await preferences.UpdatePreferencesAsync(user.Id, request.ThemeId,
                    request.LanguageTag, cancellationToken));
            }
            catch (Exception exception) when (exception is PortalValidationException or UnauthorizedAccessException)
            {
                return EndpointSecurity.PortalError(exception);
            }
        }).RequireAuthorization("Viewer")
            .Produces<PortalPreferences>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
