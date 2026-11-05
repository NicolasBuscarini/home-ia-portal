using GarageWatch.Application.Portal;

using GarageWatch.Application.Authorization;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class ListPortalModulesEndpoint() : BaseEndpoint("Portal administration", "AdminListPortalModules",
    "List registered portal modules",
    "Returns a bounded page of module manifests, including disabled modules. Administrator access is required.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/portal-modules", async (int? page, int? pageSize,
            IPortalModuleCatalog catalog, CancellationToken cancellationToken) =>
            Results.Ok(await catalog.ListAsync(page ?? 1, pageSize ?? 50, cancellationToken)))
            .RequireAuthorization(PermissionPolicy.Name("admin.modules.read"))
            .Produces<PortalModulePage>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
