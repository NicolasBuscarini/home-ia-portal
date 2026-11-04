using GarageWatch.Api.Endpoints;
using GarageWatch.Api.Live;
using GarageWatch.Application.Cameras;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Live;

public sealed class GetPlaylistEndpoint() : BaseEndpoint("Live video", "GetLivePlaylist",
    "Get authenticated live HLS playlist", "Requires an active session and camera grant (or Administrator role). Playlist entries reference only validated local spool segments.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/live/{cameraId}/index.m3u8", async (string cameraId, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, IamDbContext database, ICameraResourceAccess cameraAccess, CaptureOptions captureOptions,
            IMonitoringLiveClient liveClient, LivePlaylistSequence playlistSequence,
            CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            if (!await cameraAccess.CanReadLiveAsync(user.Id, cameraId, cancellationToken)) return Results.NotFound();
            var internalCameraId = await database.CameraCatalog.AsNoTracking().Where(item => item.PublicId == cameraId)
                .Select(item => item.CameraId).SingleOrDefaultAsync(cancellationToken);
            if (!string.Equals(internalCameraId, captureOptions.CameraId, StringComparison.Ordinal)) return Results.NotFound();
            var remotePlaylist = await liveClient.GetPlaylistAsync(internalCameraId!, cancellationToken);
            return remotePlaylist is null
                ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                : Results.Text(remotePlaylist, "application/vnd.apple.mpegurl");
        }).RequireAuthorization("Viewer")
            .Produces<string>(StatusCodes.Status200OK, "application/vnd.apple.mpegurl")
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status503ServiceUnavailable);
}
