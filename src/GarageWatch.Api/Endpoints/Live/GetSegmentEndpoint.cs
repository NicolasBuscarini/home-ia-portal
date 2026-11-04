using GarageWatch.Api.Endpoints;
using GarageWatch.Application.Cameras;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Live;

public sealed class GetSegmentEndpoint() : BaseEndpoint("Live video", "GetLiveSegment",
    "Get an authenticated HLS segment", "Checks the current camera grant before resolving a local segment and enabling byte-range responses; client paths are not accepted.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/live/{cameraId}/segments/{segmentId}", async (string cameraId, string segmentId,
            ClaimsPrincipal principal, UserManager<ApplicationUser> users, ICameraResourceAccess cameraAccess,
            IamDbContext database, CaptureOptions captureOptions,
            IMonitoringLiveClient liveClient, HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            if (!await cameraAccess.CanReadLiveAsync(user.Id, cameraId, cancellationToken)) return Results.NotFound();
            var internalCameraId = await database.CameraCatalog.AsNoTracking().Where(item => item.PublicId == cameraId)
                .Select(item => item.CameraId).SingleOrDefaultAsync(cancellationToken);
            if (!string.Equals(internalCameraId, captureOptions.CameraId, StringComparison.Ordinal) ||
                !LocalSegmentSpool.IsSafeSegmentId(segmentId)) return Results.NotFound();
            var remote = await liveClient.GetSegmentAsync(internalCameraId!, segmentId,
                httpContext.Request.Headers.Range.ToString(), cancellationToken);
            if (remote is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            httpContext.Response.StatusCode = remote.StatusCode;
            httpContext.Response.ContentType = remote.ContentType;
            if (!string.IsNullOrEmpty(remote.ContentRange))
                httpContext.Response.Headers.ContentRange = remote.ContentRange;
            await httpContext.Response.Body.WriteAsync(remote.Content, cancellationToken);
            return Results.Empty;
        }).RequireAuthorization("Viewer")
            .Produces<byte[]>(StatusCodes.Status200OK, "video/mp2t")
            .Produces<byte[]>(StatusCodes.Status206PartialContent, "video/mp2t")
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
}
