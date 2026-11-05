using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Errors;
using GarageWatch.Application.Authorization;
using GarageWatch.Application.Identity;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Api.Endpoints.Biometrics;

public sealed record CreateIdentityMatchPolicyDraftRequest(
    double? Threshold,
    double? Margin,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    string? CameraPublicId,
    string? SnapshotHash);

public sealed class GetIdentityCalibrationEndpoint() : BaseEndpoint("Biometrics", "GetIdentityCalibration",
    "Preview identity calibration", "Aggregates reviewed observations into bounded score and gap histograms for the interactive threshold/margin sliders. It returns no image, embedding, observation identifier or person list.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/identity-match-policy/calibration",
            async (DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? cameraPublicId, int? bins,
                IdentityCalibrationService calibration, CancellationToken cancellationToken) =>
            {
                var to = (toUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
                var from = (fromUtc ?? to.AddDays(-90)).ToUniversalTime();
                try
                {
                    return Results.Ok(await calibration.GetSnapshotAsync(
                        new IdentityCalibrationQuery(from, to, cameraPublicId), bins ?? IdentityCalibrationRules.DefaultBins,
                        cancellationToken));
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(new ApiErrorResponse(exception.Message));
                }
            }).RequireAuthorization(PermissionPolicy.Name("biometrics.manage"))
            .Produces<IdentityCalibrationSnapshot>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

public sealed class CreateIdentityMatchPolicyDraftEndpoint() : BaseEndpoint("Biometrics",
    "CreateIdentityMatchPolicyDraft", "Create a calibration draft",
    "Recomputes the reviewed sample server-side and stores a versioned threshold/margin draft. Nothing is applied to new observations until an explicit promotion.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/admin/identity-match-policy/drafts",
            async (CreateIdentityMatchPolicyDraftRequest request, HttpContext context, ClaimsPrincipal principal,
                UserManager<ApplicationUser> users, IdentityCalibrationService calibration,
                CancellationToken cancellationToken) =>
            {
                if (!EndpointSecurity.IsSameOrigin(context))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                var actor = await users.GetUserAsync(principal);
                if (actor is null || string.IsNullOrEmpty(actor.PublicId)) return Results.Unauthorized();
                if (request.Threshold is not { } threshold || request.Margin is not { } margin ||
                    request.FromUtc is not { } from || request.ToUtc is not { } to)
                    return Results.BadRequest(new ApiErrorResponse("invalid_identity_match_policy"));

                try
                {
                    return Results.Created(
                        "/api/v1/admin/identity-match-policy",
                        await calibration.CreateDraftAsync(actor.PublicId,
                            new IdentityMatchPolicyDraftRequest(threshold, margin, from, to,
                                request.CameraPublicId, request.SnapshotHash), cancellationToken));
                }
                catch (IdentityCalibrationConflictException exception)
                {
                    return Results.Conflict(new ApiErrorResponse(exception.Message));
                }
                catch (DbUpdateException)
                {
                    return Results.Conflict(new ApiErrorResponse("identity_match_policy_conflict"));
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(new ApiErrorResponse(exception.Message));
                }
            }).RequireAuthorization(PermissionPolicy.Name("biometrics.manage"))
            .Accepts<CreateIdentityMatchPolicyDraftRequest>("application/json")
            .Produces<IdentityMatchPolicyVersionItem>(StatusCodes.Status201Created)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

public sealed class ListIdentityMatchPolicyVersionsEndpoint() : BaseEndpoint("Biometrics",
    "ListIdentityMatchPolicyVersions", "List calibration policy versions",
    "Returns drafts, the active policy and archived versions kept for rollback; it includes aggregate counts and estimated error rates only.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/identity-match-policy",
            async (IdentityCalibrationService calibration, CancellationToken cancellationToken) =>
                Results.Ok(await calibration.ListAsync(cancellationToken)))
            .RequireAuthorization(PermissionPolicy.Name("biometrics.manage"))
            .Produces<IReadOnlyList<IdentityMatchPolicyVersionItem>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

public sealed class PromoteIdentityMatchPolicyVersionEndpoint() : BaseEndpoint("Biometrics",
    "PromoteIdentityMatchPolicyVersion", "Promote or roll back a calibration policy",
    "Explicitly promotes a draft (or an archived version, as rollback) to the policy applied to new observations. Predictions already stored are never rewritten.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/admin/identity-match-policy/{policyId}/promote",
            async (string policyId, HttpContext context, ClaimsPrincipal principal,
                UserManager<ApplicationUser> users, IdentityCalibrationService calibration,
                CancellationToken cancellationToken) =>
            {
                if (!EndpointSecurity.IsSameOrigin(context))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                var actor = await users.GetUserAsync(principal);
                if (actor is null || string.IsNullOrEmpty(actor.PublicId)) return Results.Unauthorized();

                try
                {
                    return Results.Ok(await calibration.PromoteAsync(actor.PublicId, policyId, cancellationToken));
                }
                catch (IdentityMatchPolicyNotFoundException exception)
                {
                    return Results.NotFound(new ApiErrorResponse(exception.Message));
                }
                catch (IdentityCalibrationConflictException exception)
                {
                    return Results.Conflict(new ApiErrorResponse(exception.Message));
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Results.Conflict(new ApiErrorResponse("identity_match_policy_conflict"));
                }
                catch (DbUpdateException)
                {
                    return Results.Conflict(new ApiErrorResponse("identity_match_policy_conflict"));
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(new ApiErrorResponse(exception.Message));
                }
            }).RequireAuthorization(PermissionPolicy.Name("biometrics.manage"))
            .Produces<IdentityMatchPolicyVersionItem>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
