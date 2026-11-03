using System.Net;
using System.Security.Claims;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Clips;

public sealed class GetClipThumbnailEndpoint() : BaseEndpoint("Clips", "GetClipThumbnail", "Get authorized clip thumbnail",
    "Checks current camera scope and thumbnail SHA-256 before serving JPEG content.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/clips/{publicId}/thumbnail", async (string publicId, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, ClipLibraryService library, IMonitoringClipsClient clips,
            CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var result = await library.GetAsync(user.Id, await users.IsInRoleAsync(user, "Administrator"), publicId,
                cancellationToken);
            if (result.Details is null || (result.ThumbnailPath is null && !result.RemoteThumbnail))
                return Results.NotFound();
            if (result.RemoteThumbnail)
            {
                var upstream = await clips.GetThumbnailAsync(publicId, cancellationToken);
                if (upstream is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                if (upstream.StatusCode == HttpStatusCode.NotFound)
                {
                    upstream.Dispose();
                    return Results.NotFound();
                }
                if (!upstream.IsSuccessStatusCode)
                {
                    upstream.Dispose();
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
                return new UpstreamStreamResult(upstream);
            }
            if (!await ClipLibraryService.HasValidHashAsync(result.ThumbnailPath!, result.Details.ThumbnailSha256!, cancellationToken))
                return Results.NotFound();
            return Results.File(result.ThumbnailPath!, "image/jpeg");
        }).RequireAuthorization().Produces<byte[]>(StatusCodes.Status200OK, "image/jpeg")
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
}
