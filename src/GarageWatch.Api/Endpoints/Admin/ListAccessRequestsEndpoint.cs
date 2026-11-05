using GarageWatch.Api.Endpoints;
using GarageWatch.Infrastructure.Identity;

using GarageWatch.Application.Authorization;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class ListAccessRequestsEndpoint() : BaseEndpoint("User administration", "AdminListAccessRequests",
    "List access requests", "Returns a paginated list of access requests without exposing email addresses or free-form personal content.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/access-requests", async (int? page, int? pageSize,
            AccessRequestService requests, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await requests.ListAsync(page ?? 1, pageSize ?? 25, cancellationToken));
            }
            catch (ArgumentOutOfRangeException)
            {
                return Results.BadRequest(new ApiErrorResponse("invalid_pagination"));
            }
        }).RequireAuthorization(PermissionPolicy.Name("admin.requests.read")).Produces<AccessRequestPage>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
