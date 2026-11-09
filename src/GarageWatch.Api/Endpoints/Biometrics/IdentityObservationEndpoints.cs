using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Errors;
using GarageWatch.Application.Authorization;
using GarageWatch.Application.Identity;
using GarageWatch.Application.PlatformEvents;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Api.Endpoints.Biometrics;

public sealed record ReviewIdentityObservationRequest(string? Decision, string? PersonPublicId, string? Note);

public sealed class IngestIdentityObservationEndpoint() : BaseEndpoint("Biometrics", "IngestIdentityObservation",
    "Ingest a face observation", "Accepts a minimized derived embedding from the monitoring service, matches it in memory and stores only the immutable prediction and opaque references. Requires the narrow identity.observations.write service scope.")
{
    private const int MaxBodyBytes = 256 * 1024;

    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/monitoring/identity-observations",
            async (HttpContext context, FaceObservationV1 observation, IdentityTriageService triage,
                PlatformEventServiceTokenValidator tokens, CancellationToken cancellationToken) =>
            {
                if (context.Request.ContentLength > MaxBodyBytes)
                    return Results.Json(new ApiErrorResponse("payload_too_large"),
                        statusCode: StatusCodes.Status413PayloadTooLarge);
                if (!context.Request.HasJsonContentType())
                    return Results.BadRequest(new ApiErrorResponse("invalid_face_observation"));
                var authorization = context.Request.Headers.Authorization.ToString();
                if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                    !tokens.TryValidate(authorization["Bearer ".Length..].Trim(), IdentityObservationScopes.Write,
                        out var source) ||
                    !string.Equals(source, PlatformEventSources.Monitoring, StringComparison.Ordinal))
                    return Results.Unauthorized();
                if (!observation.IsValid)
                    return Results.BadRequest(new ApiErrorResponse("invalid_face_observation"));

                try
                {
                    var result = await triage.IngestAsync(observation, cancellationToken);
                    var response = result.Observation;
                    return result.Duplicate
                        ? Results.Ok(response)
                        : Results.Created($"/api/v1/admin/identity-observations/{response.Summary.PublicId}",
                            response);
                }
                catch (IdentityMatchingUnavailableException exception)
                {
                    return Results.Json(new ApiErrorResponse(exception.Code),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }).AllowAnonymous()
            .Accepts<FaceObservationV1>("application/json")
            .Produces<IdentityObservationDetail>(StatusCodes.Status201Created)
            .Produces<IdentityObservationDetail>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status413PayloadTooLarge)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable);
}

public sealed class ListIdentityObservationsEndpoint() : BaseEndpoint("Biometrics", "ListIdentityObservations",
    "List identity observations", "Returns the triage queue and history with scores and opaque person references; it never returns embeddings or images.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/identity-observations", async (string? matchStatus, string? reviewStatus,
            string? cameraPublicId, string? clipPublicId, int? page, int? pageSize, IdentityTriageService triage,
            CancellationToken cancellationToken) =>
        {
            IdentityMatchStatus? match = null;
            IdentityObservationReviewStatus? review = null;
            if (!string.IsNullOrEmpty(matchStatus))
            {
                if (!IdentityTriageLabels.TryParseMatchStatus(matchStatus, out var parsedMatch))
                    return Results.BadRequest(new ApiErrorResponse("invalid_identity_observation_filter"));
                match = parsedMatch;
            }
            if (!string.IsNullOrEmpty(reviewStatus))
            {
                if (!IdentityTriageLabels.TryParseReviewStatus(reviewStatus, out var parsedReview))
                    return Results.BadRequest(new ApiErrorResponse("invalid_identity_observation_filter"));
                review = parsedReview;
            }

            try
            {
                var result = await triage.ListAsync(match, review, cameraPublicId, clipPublicId, page ?? 1,
                    pageSize ?? 50, cancellationToken);
                return Results.Ok(result);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new ApiErrorResponse("invalid_identity_observation_filter"));
            }
        }).RequireAuthorization(PermissionPolicy.Name("biometrics.manage"))
            .Produces<IdentityObservationPage>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

public sealed class GetIdentityObservationEndpoint() : BaseEndpoint("Biometrics", "GetIdentityObservation",
    "Get an identity observation", "Returns the immutable prediction, its bounded candidate list and append-only reviews; it never returns embeddings or images.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/identity-observations/{observationId}",
            async (string observationId, IdentityTriageService triage, CancellationToken cancellationToken) =>
            {
                try
                {
                    return Results.Ok(await triage.GetAsync(observationId, cancellationToken));
                }
                catch (IdentityObservationNotFoundException exception)
                {
                    return Results.NotFound(new ApiErrorResponse(exception.Message));
                }
            }).RequireAuthorization(PermissionPolicy.Name("biometrics.manage"))
            .Produces<IdentityObservationDetail>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

public sealed class ReviewIdentityObservationEndpoint() : BaseEndpoint("Biometrics", "ReviewIdentityObservation",
    "Review an identity observation", "Appends a confirm, reject, inconclusive or associated-person decision. The original prediction is preserved and no model is retrained or promoted.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/admin/identity-observations/{observationId}/reviews",
            async (string observationId, ReviewIdentityObservationRequest request, HttpContext context,
                ClaimsPrincipal principal, UserManager<ApplicationUser> users, IdentityTriageService triage,
                CancellationToken cancellationToken) =>
            {
                if (!EndpointSecurity.IsSameOrigin(context))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                var actor = await users.GetUserAsync(principal);
                if (actor is null || string.IsNullOrEmpty(actor.PublicId)) return Results.Unauthorized();
                if (!IdentityTriageLabels.TryParseDecision(request.Decision, out var decision))
                    return Results.BadRequest(new ApiErrorResponse("invalid_identity_review_decision"));

                try
                {
                    return Results.Ok(await triage.ReviewAsync(actor.Id, actor.PublicId, observationId, decision,
                        request.PersonPublicId, request.Note, cancellationToken));
                }
                catch (IdentityObservationNotFoundException exception)
                {
                    return Results.NotFound(new ApiErrorResponse(exception.Message));
                }
                catch (IdentityObservationConflictException exception)
                {
                    return Results.Conflict(new ApiErrorResponse(exception.Message));
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Results.Conflict(new ApiErrorResponse("identity_observation_conflict"));
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest(new ApiErrorResponse("invalid_identity_review"));
                }
            }).RequireAuthorization(PermissionPolicy.Name("biometrics.manage"))
            .Accepts<ReviewIdentityObservationRequest>("application/json")
            .Produces<IdentityObservationReviewItem>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
