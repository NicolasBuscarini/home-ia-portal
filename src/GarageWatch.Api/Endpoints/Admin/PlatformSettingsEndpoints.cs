using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Endpoints;
using GarageWatch.Application.Identity;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed record UpdatePlatformSettingsRequest(int ExpectedVersion, PlatformSettingsValues Values);

public sealed class GetPlatformSettingsEndpoint()
    : BaseEndpoint(
        "Platform settings",
        "GetPlatformSettings",
        "Returns the persisted platform limits and their current version.",
        "Administrator-only view of non-secret operational limits. Values are applied when the API starts.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet(
                "/api/v1/admin/platform-settings",
                (IConfiguration configuration) => Results.Ok(PlatformSettingsFile.LoadOrCreate(configuration)))
            .RequireAuthorization("Administrator")
            .Produces<PlatformSettingsDocument>();
}

public sealed class UpdatePlatformSettingsEndpoint()
    : BaseEndpoint(
        "Platform settings",
        "UpdatePlatformSettings",
        "Persists platform limits with optimistic concurrency.",
        "Administrator-only mutation of non-secret operational limits. The API must be restarted to apply the new values.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPut(
                "/api/v1/admin/platform-settings",
                async (
                    HttpContext context,
                    ClaimsPrincipal principal,
                    UpdatePlatformSettingsRequest request,
                    IConfiguration configuration,
                    UserManager<ApplicationUser> users,
                    IIamUnitOfWork unitOfWork,
                    IPlatformEventRepository audit,
                    TimeProvider timeProvider) =>
                {
                    if (!EndpointSecurity.IsSameOrigin(context))
                        return Results.StatusCode(StatusCodes.Status403Forbidden);

                    var actor = await users.GetUserAsync(principal);
                    if (actor is null) return Results.Unauthorized();

                    PlatformSettingsDocument updated;
                    try
                    {
                        updated = PlatformSettingsFile.Save(
                            configuration,
                            request.ExpectedVersion,
                            request.Values);
                    }
                    catch (PlatformSettingsConflictException)
                    {
                        return Results.Conflict(new ApiErrorResponse("platform_settings_version_conflict"));
                    }
                    catch (PlatformSettingsValidationException exception)
                    {
                        return Results.BadRequest(new ApiErrorResponse(exception.Message));
                    }

                    await unitOfWork.ExecuteAtomicallyAsync(
                        token =>
                        {
                            audit.Append(
                                actor.Id,
                                actor.Id,
                                "platform.settings.updated",
                                "platform-settings",
                                timeProvider.GetUtcNow());
                            return Task.CompletedTask;
                        },
                        context.RequestAborted);

                    return Results.Ok(updated);
                })
            .RequireAuthorization("Administrator")
            .Produces<PlatformSettingsDocument>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);
}
