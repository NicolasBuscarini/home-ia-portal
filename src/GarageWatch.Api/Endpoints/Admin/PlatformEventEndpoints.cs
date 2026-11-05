using GarageWatch.Application.PlatformEvents;
using GarageWatch.Infrastructure.Identity;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class ListPlatformEventsEndpoint() : BaseEndpoint("Event administration",
    "AdminListPlatformEvents", "List platform events",
    "Lists platform events with structured filters and a keyset cursor. Results never include request payloads, media, embeddings or secrets.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/events", async (HttpContext context,
            IPlatformEventQueryService queries, IPlatformEventRecorder recorder,
            TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            if (!PlatformEventQueryParameters.TryCreate(context.Request.Query,
                    timeProvider.GetUtcNow(), out var query))
                return Results.BadRequest(new ApiErrorResponse("invalid_event_query"));

            var page = await queries.ListAsync(query, cancellationToken);
            await recorder.RecordRequiredAsync(new PlatformEventDraft(
                "event_store.queried", PlatformEventOutcomes.Success,
                Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["pageSize"] = query.PageSize,
                    ["resultCount"] = page.Items.Count,
                    ["hasCursor"] = query.Cursor is not null
                }), cancellationToken);
            return Results.Ok(page);
        }).RequireAuthorization("Administrator")
            .Produces<PlatformEventPage>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

public sealed class GetPlatformEventEndpoint() : BaseEndpoint("Event administration",
    "AdminGetPlatformEvent", "Get a platform event",
    "Returns one platform event with its integrity check and typed metadata; request payloads and secrets are never stored.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/events/{publicId}", async (string publicId,
            IPlatformEventQueryService queries, IPlatformEventRecorder recorder,
            CancellationToken cancellationToken) =>
        {
            if (!PlatformEventPublicId.IsValid(publicId))
                return Results.NotFound(new ApiErrorResponse("unknown_event"));
            var detail = await queries.GetAsync(publicId, cancellationToken);
            if (detail is null) return Results.NotFound(new ApiErrorResponse("unknown_event"));

            await recorder.RecordRequiredAsync(new PlatformEventDraft(
                "event_store.viewed", PlatformEventOutcomes.Success,
                Resource: new PlatformEventReferenceV1("event", publicId)),
                cancellationToken);
            return Results.Ok(detail);
        }).RequireAuthorization("Administrator")
            .Produces<PlatformEventDetail>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
