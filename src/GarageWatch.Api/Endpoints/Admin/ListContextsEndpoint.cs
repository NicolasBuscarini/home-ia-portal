using GarageWatch.Api.Endpoints;
using GarageWatch.Application.Cameras;

using GarageWatch.Application.Authorization;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class ListContextsEndpoint() : BaseEndpoint("Camera administration", "AdminListContexts",
    "List camera contexts", "Returns a stable, bounded page of camera contexts; Administrator access is required.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/contexts", async (int? page, int? pageSize, ICameraCatalog catalog,
            CancellationToken cancellationToken) =>
            Results.Ok(await catalog.ListContextsAsync(page ?? 1, pageSize ?? 50, cancellationToken)))
            .RequireAuthorization(PermissionPolicy.Name("admin.cameras.read"))
            .Produces<CatalogPage<CameraContextRecord>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
