using GarageWatch.Api.Endpoints;
using GarageWatch.Application.Authorization;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class ListPermissionCatalogEndpoint() : BaseEndpoint("Access profiles", "AdminListPermissionCatalog",
    "List permission catalog", "Returns the finite catalog with module, risk, scope kinds, availability and owner-only metadata so the administrative UI can explain what is delegable.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/permission-catalog", () =>
            Results.Ok(PermissionCatalog.All))
            .RequireAuthorization(PermissionPolicy.Name("admin.permissions.read"))
            .Produces<IReadOnlyCollection<PermissionDefinition>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
