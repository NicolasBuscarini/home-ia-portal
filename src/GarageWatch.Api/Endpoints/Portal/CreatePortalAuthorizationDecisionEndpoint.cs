using GarageWatch.Api.Contracts;
using GarageWatch.Application.Portal;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Portal;

public sealed class CreatePortalAuthorizationDecisionEndpoint() : BaseEndpoint("Portal",
    "CreatePortalAuthorizationDecision", "Evaluate a portal authorization decision",
    "Evaluates requested permission keys and public camera/context scopes for the authenticated account and a registered module audience. Read-only; it does not issue a service token.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/portal/authorization/decisions", async (
            PortalAuthorizationDecisionRequest request, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, IPortalAuthorizationDecisions decisions,
            CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            try
            {
                var decisionRequest = new PortalAuthorizationRequest(request.Audience, request.ResidenceId,
                    request.PermissionKeys ?? [], request.Scopes?.Select(scope =>
                        new PortalAuthorizationScope(scope.ContextId, scope.CameraId)).ToArray() ?? []);
                return Results.Ok(await decisions.DecideAsync(user.Id, decisionRequest, cancellationToken));
            }
            catch (Exception exception) when (exception is PortalValidationException or PortalNotFoundException or
                                              UnauthorizedAccessException)
            {
                return EndpointSecurity.PortalError(exception);
            }
        }).RequireAuthorization("Viewer")
            .Produces<PortalAuthorizationDecision>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
