using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Errors;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

using GarageWatch.Application.Authorization;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class LinkBiometricPersonEndpoint() : BaseEndpoint("Biometrics", "AdminLinkBiometricPerson",
    "Link an enrollment to a roster person", "Associates an encrypted biometric enrollment with an authorized roster person; it never copies the embedding or exposes the person to non-administrators.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPut("/api/v1/admin/biometrics/enrollments/{enrollmentId}/person",
            async (string enrollmentId, LinkBiometricPersonRequest request, HttpContext context,
                ClaimsPrincipal principal, UserManager<ApplicationUser> users,
                BiometricPersonLinkService links, CancellationToken cancellationToken) =>
            {
                if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
                var actor = await users.GetUserAsync(principal);
                if (actor is null) return Results.Unauthorized();
                try
                {
                    var link = await links.LinkAsync(actor.Id, enrollmentId, request.PersonPublicId,
                        cancellationToken);
                    return Results.Ok(new BiometricPersonLinkResponse(link.EnrollmentPublicId,
                        link.KnownPersonPublicId, link.LinkedAtUtc));
                }
                catch (KeyNotFoundException)
                {
                    return Results.NotFound(new ApiErrorResponse("biometric_person_link_target_not_found"));
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest(new ApiErrorResponse("invalid_biometric_person_link"));
                }
                catch (BiometricEnrollmentUnavailableException exception)
                {
                    return Results.Json(new ApiErrorResponse(exception.Message),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                catch (MonitoringRosterUnavailableException)
                {
                    return Results.Json(new ApiErrorResponse("monitoring_roster_unavailable"),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }).RequireAuthorization(PermissionPolicy.Name("admin.people.manage"))
            .Produces<BiometricPersonLinkResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

public sealed class UnlinkBiometricPersonEndpoint() : BaseEndpoint("Biometrics", "AdminUnlinkBiometricPerson",
    "Unlink an enrollment from a roster person", "Removes the active roster association and keeps the prior link as an audit-backed tombstone; it is idempotent.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/api/v1/admin/biometrics/enrollments/{enrollmentId}/person",
            async (string enrollmentId, HttpContext context, ClaimsPrincipal principal,
                UserManager<ApplicationUser> users, BiometricPersonLinkService links,
                CancellationToken cancellationToken) =>
            {
                if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
                var actor = await users.GetUserAsync(principal);
                if (actor is null) return Results.Unauthorized();
                try
                {
                    await links.UnlinkAsync(actor.Id, enrollmentId, cancellationToken);
                    return Results.NoContent();
                }
                catch (KeyNotFoundException)
                {
                    return Results.NotFound(new ApiErrorResponse("biometric_person_link_target_not_found"));
                }
            }).RequireAuthorization(PermissionPolicy.Name("admin.people.manage"))
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
