using GarageWatch.Api.Errors;
using GarageWatch.Application.Authorization;
using GarageWatch.Application.Identity;
using GarageWatch.Application.Monitoring;
using GarageWatch.Application.PlatformEvents;
using GarageWatch.Infrastructure.Identity;

namespace GarageWatch.Api.Endpoints.Biometrics;

/// <summary>
/// Review path for identity triage: the reviewer of an observation sees the same deterministic frame
/// positions that the face observation source sampled from the clip. Frames are rendered on demand by
/// the monitoring service and never stored; offsets alone cross this boundary.
/// </summary>
public sealed class GetIdentityObservationFramesEndpoint() : BaseEndpoint("Biometrics",
    "GetIdentityObservationFrames", "List the sampled clip frames of an identity observation",
    "Recomputes the frame offsets used by the face observation source for the observation clip; it never returns image data or embeddings.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/identity-observations/{observationId}/frames",
                async (string observationId, IdentityTriageService triage,
                    IMonitoringClipFramesClient frames, CancellationToken cancellationToken) =>
                {
                    IdentityObservationDetail detail;
                    try
                    {
                        detail = await triage.GetAsync(observationId, cancellationToken);
                    }
                    catch (IdentityObservationNotFoundException exception)
                    {
                        return Results.NotFound(new ApiErrorResponse(exception.Message));
                    }

                    if (string.IsNullOrWhiteSpace(detail.Summary.ClipPublicId))
                        return Results.Conflict(new ApiErrorResponse("identity_observation_without_clip"));

                    var manifest = await frames.GetManifestAsync(detail.Summary.ClipPublicId, cancellationToken);
                    return manifest is null
                        ? Results.Json(new ApiErrorResponse("clip_frames_unavailable"),
                            statusCode: StatusCodes.Status503ServiceUnavailable)
                        : Results.Ok(manifest);
                })
            .RequireAuthorization(PermissionPolicy.Name("biometrics.manage"), PermissionPolicy.Name("clips.read"))
            .Produces<MonitoringClipFrameManifest>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

public sealed class GetIdentityObservationFrameEndpoint() : BaseEndpoint("Biometrics",
    "GetIdentityObservationFrame", "Read one sampled clip frame of an identity observation",
    "Streams the JPEG of one sampled frame on demand, with no copy on disk, and audits the access; the media is authorized by both biometrics.manage and clips.read.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/identity-observations/{observationId}/frames/{index:int}",
                async (string observationId, int index, HttpContext context, IdentityTriageService triage,
                    IMonitoringClipFramesClient frames, IPlatformEventRecorder recorder,
                    CancellationToken cancellationToken) =>
                {
                    if (index is < 0 or > ClipFrameReadLimits.MaximumIndex)
                        return Results.BadRequest(new ApiErrorResponse("invalid_frame_index"));

                    IdentityObservationDetail detail;
                    try
                    {
                        detail = await triage.GetAsync(observationId, cancellationToken);
                    }
                    catch (IdentityObservationNotFoundException exception)
                    {
                        return Results.NotFound(new ApiErrorResponse(exception.Message));
                    }

                    if (string.IsNullOrWhiteSpace(detail.Summary.ClipPublicId))
                        return Results.Conflict(new ApiErrorResponse("identity_observation_without_clip"));

                    var image = await frames.GetFrameAsync(detail.Summary.ClipPublicId, index, cancellationToken);
                    if (image is null)
                        return Results.Json(new ApiErrorResponse("clip_frame_unavailable"),
                            statusCode: StatusCodes.Status503ServiceUnavailable);

                    // Register late so the file result cannot replace the privacy header.
                    context.Response.OnStarting(static state =>
                    {
                        ((HttpResponse)state).Headers.CacheControl = "private, no-store";
                        return Task.CompletedTask;
                    }, context.Response);
                    await recorder.RecordRequiredAsync(new PlatformEventDraft(
                        "identity.observation.frame.viewed", PlatformEventOutcomes.Success,
                        Resource: new PlatformEventReferenceV1("identity_observation", observationId)),
                        cancellationToken);
                    return Results.File(image.Content, image.ContentType);
                })
            .RequireAuthorization(PermissionPolicy.Name("biometrics.manage"), PermissionPolicy.Name("clips.read"))
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

public static class ClipFrameReadLimits
{
    /// <summary>Mirrors the monitoring sampler bound so an out-of-range index is rejected before I/O.</summary>
    public const int MaximumIndex = 11;
}
