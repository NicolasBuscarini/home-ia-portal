using GarageWatch.Api.Contracts;
using GarageWatch.Application.Portal;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

using GarageWatch.Application.Authorization;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class CreatePortalModuleEndpoint() : BaseEndpoint("Portal administration", "AdminCreatePortalModule",
    "Register a portal module",
    "Registers a versioned module manifest with menu metadata. Requires Administrator and same-origin Origin; mutation is audited.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/admin/portal-modules", async (CreatePortalModuleRequest request, HttpContext context,
            ClaimsPrincipal principal, UserManager<ApplicationUser> users, IPortalModuleCatalog catalog,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null || !actor.IsActive) return Results.Unauthorized();
            try
            {
                var created = await catalog.CreateAsync(actor.Id, ToUpsert(request, request.ModuleId),
                    cancellationToken);
                return Results.Created($"/api/v1/admin/portal-modules/{created.ModuleId}", created);
            }
            catch (Exception exception) when (exception is PortalValidationException or PortalConflictException or
                                              PortalNotFoundException or UnauthorizedAccessException)
            {
                return EndpointSecurity.PortalError(exception);
            }
        }).RequireAuthorization(PermissionPolicy.Name("admin.modules.manage"))
            .Produces<PortalModuleRecord>(StatusCodes.Status201Created)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);

    internal static PortalModuleUpsert ToUpsert(CreatePortalModuleRequest request, string moduleId) =>
        new(moduleId, request.DisplayNameKey, request.ContractVersion, request.ApiBaseUrl, request.HealthPath,
            request.RequiredPermission, request.MenuGroup, request.MenuOrder, request.MenuIcon,
            request.MenuLabelKey, request.IsEnabled,
            request.Items?.Select(item => new PortalModuleItemUpsert(item.ItemId, item.LabelKey, item.SortOrder,
                item.RequiredPermission)).ToArray());
}
