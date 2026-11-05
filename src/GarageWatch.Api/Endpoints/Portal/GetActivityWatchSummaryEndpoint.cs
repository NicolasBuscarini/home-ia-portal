using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Application.Authorization;
using GarageWatch.Application.Identity;
using GarageWatch.Application.PlatformEvents;
using GarageWatch.Application.Portal;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Identity.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Api.Endpoints.Portal;

public sealed class GetActivityWatchSummaryEndpoint() : BaseEndpoint("Portal", "GetActivityWatchSummary",
    "Read the minimized ActivityWatch summary",
    "Returns aggregate application usage from the external ActivityWatch service for the configured window. Reads are authenticated, permission-gated, audited and isolated: an unavailable or malformed upstream yields a sanitized 503 without exposing payloads, titles or credentials.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/portal/modules/activitywatch/summary", async (ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, AccessProfileService profiles, IPermissionEvaluator evaluator,
            IActivityWatchModule activityWatch, IamDbContext database, IPlatformEventRecorder recorder,
            TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var moduleEnabled = await database.PortalModules.AsNoTracking()
                .AnyAsync(module => module.ModuleId == "activitywatch" && module.IsEnabled, cancellationToken);
            if (!moduleEnabled) return Results.NotFound(new ApiErrorResponse("portal_resource_not_found"));

            var subject = await profiles.GetSubjectAsync(user.Id, cancellationToken);
            if (!subject.IsActive ||
                !evaluator.IsAllowed(subject, new PermissionRequest("activitywatch.read")))
            {
                await recorder.RecordRequiredAsync(new PlatformEventDraft(
                    "activitywatch.read.denied", PlatformEventOutcomes.Denied,
                    Resource: new PlatformEventReferenceV1("portal.module", "activitywatch")), cancellationToken);
                await database.SaveChangesAsync(cancellationToken);
                return Results.Forbid();
            }

            var summary = await activityWatch.GetSummaryAsync(cancellationToken);
            await recorder.RecordRequiredAsync(new PlatformEventDraft(
                summary.Reachable ? "activitywatch.read" : "activitywatch.read.unavailable",
                summary.Reachable ? PlatformEventOutcomes.Success : PlatformEventOutcomes.Error,
                Resource: new PlatformEventReferenceV1("portal.module", "activitywatch")), cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            return Results.Json(summary, statusCode: summary.Reachable
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable);
        }).RequireAuthorization("Viewer")
            .Produces<ActivityWatchSummary>(StatusCodes.Status200OK)
            .Produces<ActivityWatchSummary>(StatusCodes.Status503ServiceUnavailable)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
