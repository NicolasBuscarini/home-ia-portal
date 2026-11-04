using GarageWatch.Api.Contracts;
using GarageWatch.Api.Errors;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Monitoring;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class GetMonitoringCaptureEndpoint() : BaseEndpoint("Monitoring", "GetMonitoringCapture",
    "Read capture runtime settings",
    "Returns the remote monitoring capture profile/state without RTSP credentials. Requires an Administrator.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/monitoring/capture", async (IMonitoringCaptureClient capture,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await capture.GetCaptureAsync(cancellationToken);
            return snapshot is null
                ? Results.Json(new ApiErrorResponse("monitoring_capture_unavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(snapshot);
        }).RequireAuthorization("Administrator")
            .Produces<MonitoringCaptureSnapshot>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

public sealed class UpdateMonitoringCaptureEndpoint() : BaseEndpoint("Monitoring", "UpdateMonitoringCapture",
    "Update capture runtime settings",
    "Updates non-secret capture limits/enabled state with validation. RTSP and credentials stay in environment.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPut("/api/v1/admin/monitoring/capture/settings", async (
            MonitoringCaptureSettings settings, HttpContext context,
            IMonitoringCaptureClient capture, CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (settings.SegmentSeconds is < 1 or > 60 ||
                settings.MaximumSegments is < 1 or > 10_000 ||
                settings.MaximumSegmentBytes is < 1_048_576 or > 1_073_741_824)
                return Results.BadRequest(new ApiErrorResponse("invalid_capture_settings"));
            var updated = await capture.UpdateCaptureAsync(settings, cancellationToken);
            return updated is null
                ? Results.Json(new ApiErrorResponse("monitoring_capture_unavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(updated);
        }).RequireAuthorization("Administrator")
            .Produces<MonitoringCaptureSettings>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
