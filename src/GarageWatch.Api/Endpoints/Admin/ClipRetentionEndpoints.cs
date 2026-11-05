using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Errors;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

using GarageWatch.Application.Authorization;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class GetClipRetentionEndpoint() : BaseEndpoint("Clip retention", "GetClipRetention",
    "Get camera clip retention policy", "Returns versioned retention settings for one camera.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/cameras/{cameraId}/clip-retention", async (string cameraId,
            IClipRetentionGateway retention, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await retention.GetSettingsAsync(cameraId, cancellationToken)); }
            catch (KeyNotFoundException) { return Results.NotFound(new ApiErrorResponse("camera_not_found")); }
            catch (MonitoringRetentionUnavailableException) { return Results.Json(new ApiErrorResponse("monitoring_retention_unavailable"), statusCode: StatusCodes.Status503ServiceUnavailable); }
        }).RequireAuthorization(PermissionPolicy.Name("admin.retention.manage")).Produces<ClipRetentionSettings>()
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable);
}

public sealed class UpdateClipRetentionEndpoint() : BaseEndpoint("Clip retention", "UpdateClipRetention",
    "Update camera clip retention policy", "Persists per-camera retention age and archive quota with optimistic concurrency.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPut("/api/v1/admin/cameras/{cameraId}/clip-retention", async (string cameraId,
            UpdateClipRetentionRequest request, HttpContext context, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, IClipRetentionGateway retention, CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            try
            {
                return Results.Ok(await retention.UpdateSettingsAsync(actor.Id, cameraId, request.ExpectedVersion,
                    request.RetentionDays, request.MaximumStorageGigabytes, cancellationToken));
            }
            catch (ArgumentException) { return Results.BadRequest(new ApiErrorResponse("invalid_clip_retention_policy")); }
            catch (KeyNotFoundException) { return Results.NotFound(new ApiErrorResponse("camera_not_found")); }
            catch (ClipRetentionConflictException exception) { return Results.Conflict(new ApiErrorResponse(exception.Message)); }
            catch (MonitoringRetentionUnavailableException) { return Results.Json(new ApiErrorResponse("monitoring_retention_unavailable"), statusCode: StatusCodes.Status503ServiceUnavailable); }
        }).RequireAuthorization(PermissionPolicy.Name("admin.retention.manage")).Produces<ClipRetentionSettings>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
