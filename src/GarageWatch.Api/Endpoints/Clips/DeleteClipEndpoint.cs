using System.Security.Claims;
using GarageWatch.Api.Errors;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Clips;

public sealed class DeleteClipEndpoint() : BaseEndpoint("Clips", "DeleteClip", "Delete one clip",
    "Permanently deletes an authorized ready clip from the local spool after permission and preservation checks.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/api/v1/clips/{clipId}", async (string clipId, HttpContext context,
            ClaimsPrincipal principal, UserManager<ApplicationUser> users, ClipDeletionService deletion,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            var results = await deletion.DeleteAsync(actor.Id, [clipId], cancellationToken);
            var result = results.Single();
            if (result.Deleted) return Results.NoContent();
            return result.Code switch
            {
                "clip_not_found" => Results.NotFound(new ApiErrorResponse("clip_not_found")),
                "clip_preserved" => Results.Conflict(new ApiErrorResponse("clip_preserved")),
                "clip_archived" => Results.Conflict(new ApiErrorResponse("clip_archived")),
                "clip_not_ready" => Results.Conflict(new ApiErrorResponse("clip_not_ready")),
                "clip_delete_unavailable" => Results.Json(new ApiErrorResponse("clip_delete_unavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Json(new ApiErrorResponse("clip_delete_failed"),
                    statusCode: StatusCodes.Status503ServiceUnavailable)
            };
        }).RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
