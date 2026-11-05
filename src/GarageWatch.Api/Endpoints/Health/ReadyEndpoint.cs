using GarageWatch.Api.Endpoints;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Monitoring;

using GarageWatch.Application.Authorization;

namespace GarageWatch.Api.Endpoints.Health;

public sealed class ReadyEndpoint() : BaseEndpoint("Health", "GetReadiness", "Check capture readiness",
    "Reports sanitized capture and spool state. Requires an authenticated Administrator; returns 503 while capture is degraded.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/health/ready", async (CaptureOptions options,
            IMonitoringModuleClient monitoring, CancellationToken cancellationToken) =>
        {
            {
                var remote = await monitoring.GetCameraStatusAsync(options.CameraId, cancellationToken);
                var remoteState = remote.Reachable ? remote.Status : "unreachable";
                var remoteReady = remoteState == "capturing";
                return Results.Json(new ReadinessResponse(remoteReady ? "ready" : "degraded",
                    new CaptureReadinessResponse("remote-monitoring", "external", ["remote-capture"],
                        remoteState, remote.StreamEpoch, remote.Gaps, remote.Segments,
                        remote.Reachable ? remote.ReasonCode : remote.ErrorCode,
                        new SpoolReadinessResponse(remote.Reachable ? "available" : "unavailable",
                            remote.Segments, options.MaximumSegments))),
                    statusCode: remoteReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireAuthorization(PermissionPolicy.Name("admin.system.health.read"))
            .Produces<ReadinessResponse>(StatusCodes.Status200OK)
            .Produces<ReadinessResponse>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
