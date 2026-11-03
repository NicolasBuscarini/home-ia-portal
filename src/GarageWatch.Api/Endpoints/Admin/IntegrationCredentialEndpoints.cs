using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Errors;
using GarageWatch.Application.Portal;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Integrations;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Admin;

/// <summary>
/// Shared helpers for the portal-owned integration credential endpoints (P32). Status responses are
/// metadata only; the secret is accepted on write and never returned.
/// </summary>
internal static class IntegrationCredentialProjection
{
    public static IntegrationCredentialStatusResponse Project(IntegrationCredentialStatus status,
        ActivityWatchOptions activityWatchOptions)
    {
        var source = status.Configured
            ? "database"
            : activityWatchOptions.ApiToken is not null
                ? "environment"
                : "none";
        return new(status.IntegrationId, status.Configured || source == "environment", source,
            status.KeyVersion, status.UpdatedAtUtc, status.Version);
    }
}

public sealed class GetIntegrationCredentialEndpoint() : BaseEndpoint("Integrations", "GetIntegrationCredential",
    "Read integration credential status",
    "Reports whether a portal-owned credential is stored and which key version sealed it. Never returns the secret; a configured environment token is only reported as source. Requires an Administrator.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/integrations/{integrationId}/credential", async (string integrationId,
            IIntegrationCredentialStore credentials, ActivityWatchOptions activityWatchOptions,
            CancellationToken cancellationToken) =>
        {
            if (!IntegrationCredentialIds.IsSupported(IntegrationCredentialIds.Normalize(integrationId)))
                return Results.NotFound(new ApiErrorResponse("unknown_integration"));
            var status = await credentials.GetStatusAsync(integrationId, cancellationToken);
            return Results.Ok(IntegrationCredentialProjection.Project(status, activityWatchOptions));
        }).RequireAuthorization("Administrator")
            .Produces<IntegrationCredentialStatusResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

public sealed class SetIntegrationCredentialEndpoint() : BaseEndpoint("Integrations", "SetIntegrationCredential",
    "Store or rotate an integration credential",
    "Encrypts the supplied secret with the active key version, replaces the previous value and audits the change. Same-origin Administrator only; the response never contains the secret.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPut("/api/v1/admin/integrations/{integrationId}/credential", async (string integrationId,
            SetIntegrationCredentialRequest request, HttpContext context, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, IIntegrationCredentialStore credentials,
            ActivityWatchOptions activityWatchOptions, CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var normalized = IntegrationCredentialIds.Normalize(integrationId);
            if (!IntegrationCredentialIds.IsSupported(normalized))
                return Results.NotFound(new ApiErrorResponse("unknown_integration"));
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            try
            {
                var status = await credentials.SetAsync(actor.Id, normalized, request.Secret,
                    cancellationToken);
                return Results.Ok(IntegrationCredentialProjection.Project(status, activityWatchOptions));
            }
            catch (IntegrationCredentialInputException)
            {
                return Results.BadRequest(new ApiErrorResponse("invalid_integration_credential"));
            }
            catch (IntegrationCredentialEncryptionUnavailableException)
            {
                return Results.Json(new ApiErrorResponse("integration_credential_encryption_unavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireAuthorization("Administrator")
            .Produces<IntegrationCredentialStatusResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);

}

public sealed class RemoveIntegrationCredentialEndpoint() : BaseEndpoint("Integrations", "RemoveIntegrationCredential",
    "Remove an integration credential",
    "Deletes the stored secret so the consumer falls back to the documented environment setting. Same-origin Administrator only and audited when a value existed.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/api/v1/admin/integrations/{integrationId}/credential", async (string integrationId,
            HttpContext context, ClaimsPrincipal principal, UserManager<ApplicationUser> users,
            IIntegrationCredentialStore credentials, ActivityWatchOptions activityWatchOptions,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var normalized = IntegrationCredentialIds.Normalize(integrationId);
            if (!IntegrationCredentialIds.IsSupported(normalized))
                return Results.NotFound(new ApiErrorResponse("unknown_integration"));
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            var status = await credentials.RemoveAsync(actor.Id, normalized, cancellationToken);
            return Results.Ok(IntegrationCredentialProjection.Project(status, activityWatchOptions));
        }).RequireAuthorization("Administrator")
            .Produces<IntegrationCredentialStatusResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
