using System.Security.Claims;
using GarageWatch.Api.Errors;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Clips;

public sealed class DeleteAllClipsEndpoint() : BaseEndpoint("Clips", "DeleteAllClips", "Delete all eligible clips",
    "Administrator-only permanent deletion of every ready, non-preserved and non-archived clip in the spool, including clips not loaded in the UI. Returns aggregate counts without identifiers.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/clips/delete-all", async (HttpContext context, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, ClipDeletionService deletion,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            if (!await users.IsInRoleAsync(actor, "Administrator")) return Results.Forbid();
            var result = await deletion.DeleteAllAsync(actor.Id, cancellationToken);
            return result is null
                ? Results.Json(new ApiErrorResponse("clip_delete_unavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(result);
        }).RequireAuthorization()
            .Produces(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
