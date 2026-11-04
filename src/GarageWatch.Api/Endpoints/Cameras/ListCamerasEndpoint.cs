using GarageWatch.Api.Endpoints;
using GarageWatch.Application.Cameras;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Cameras;

public sealed class ListCamerasEndpoint() : BaseEndpoint("Cameras", "ListCameras", "List cameras visible to the session",
    "Administrators see all catalog entries. Other monitoring sessions see only active grants for enabled cameras and contexts.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/cameras", async (ClaimsPrincipal principal, UserManager<ApplicationUser> users,
            ICameraCatalog catalog, IamDbContext database, CaptureOptions options,
            IMonitoringModuleClient monitoring, MonitoringModuleOptions monitoringOptions,
            CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var isAdministrator = await users.IsInRoleAsync(user, "Administrator");
            var cameras = await catalog.ListVisibleCamerasAsync(user.Id, isAdministrator, cancellationToken);
            var internalCameraIds = await database.CameraCatalog.AsNoTracking()
                .Where(item => cameras.Select(camera => camera.CameraId).Contains(item.PublicId!))
                .Select(item => new { item.PublicId, item.CameraId }).ToDictionaryAsync(item => item.PublicId!, item => item.CameraId,
                    cancellationToken);
            var activeCamera = cameras.SingleOrDefault(camera =>
                internalCameraIds.GetValueOrDefault(camera.CameraId) == options.CameraId);
            var (status, epoch) = await ReadActiveStatusAsync(activeCamera?.CameraId, options,
                monitoring, cancellationToken);
            return Results.Ok(cameras.Select(camera => new CameraSummaryResponse(camera.CameraId, camera.ContextId,
                camera.Name, camera.CameraId == activeCamera?.CameraId ? status : "stopped",
                camera.CameraId == activeCamera?.CameraId ? epoch : null,
                $"/api/v1/live/{camera.CameraId}/index.m3u8")));
        }).RequireAuthorization("Viewer").Produces<IReadOnlyList<CameraSummaryResponse>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);

    private static async Task<(string Status, string? Epoch)> ReadActiveStatusAsync(string? activeCameraPublicId,
        CaptureOptions captureOptions, IMonitoringModuleClient monitoring,
        CancellationToken cancellationToken)
    {
        if (activeCameraPublicId is null) return ("stopped", null);

        var remote = await monitoring.GetCameraStatusAsync(captureOptions.CameraId, cancellationToken);
        if (!remote.Reachable) return ("stopped", null);
        var status = remote.Status switch
        {
            "capturing" => "capturing",
            "stale" => "reconnecting",
            _ => "stopped"
        };
        return (status, remote.StreamEpoch);
    }
}
