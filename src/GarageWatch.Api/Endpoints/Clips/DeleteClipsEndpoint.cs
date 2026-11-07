using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Errors;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Clips;

public sealed record DeleteClipsRequest(IReadOnlyList<string> ClipIds);

public sealed class DeleteClipsEndpoint() : BaseEndpoint("Clips", "DeleteClips", "Delete several clips",
    "Permanently deletes up to 100 authorized ready clips, reporting per-clip refusal without leaking unauthorized clips.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/clips/delete", async (DeleteClipsRequest request, HttpContext context,
            ClaimsPrincipal principal, UserManager<ApplicationUser> users, ClipDeletionService deletion,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            var ids = request.ClipIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
            if (ids.Length == 0 || ids.Length > 100)
                return Results.BadRequest(new ApiErrorResponse("invalid_clip_selection"));
            var results = await deletion.DeleteAsync(actor.Id, ids, cancellationToken);
            return Results.Ok(new
            {
                deleted = results.Where(item => item.Deleted).Select(item => item.ClipId).ToArray(),
                failed = results.Where(item => !item.Deleted)
                    .Select(item => new { clipId = item.ClipId, code = item.Code ?? "clip_delete_failed" }).ToArray()
            });
        }).RequireAuthorization()
            .Produces(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
