using GarageWatch.Api.Contracts;
using GarageWatch.Application.Authorization;
using GarageWatch.Api.Endpoints;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class ReplaceUserAccessProfilesEndpoint() : BaseEndpoint("User administration", "AdminReplaceUserAccessProfiles",
    "Assign access profiles to a user", "Replaces profile assignments and their explicit camera/context scopes atomically. Permissions are not editable on a user; Administrator is never assignable.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPut("/api/v1/admin/users/{userId}/access-rules", async (string userId,
            SetUserAccessProfilesRequest request, HttpContext context, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, AccessProfileService profiles, IamDbContext database,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            if (request.Assignments is null) return Results.BadRequest(new ApiErrorResponse("invalid_assignments"));
            try
            {
                if (!PublicIdFormat.IsValid(userId, PublicIdPrefixes.UserPrefix))
                    return Results.NotFound(new ApiErrorResponse("user_not_found"));
                var targetId = await database.Users.Where(item => item.PublicId == userId)
                    .Select(item => (Guid?)item.Id).SingleOrDefaultAsync(cancellationToken);
                if (targetId is null) return Results.NotFound(new ApiErrorResponse("user_not_found"));
                var versionIds = request.Assignments.Select(item => item.ProfileVersionPublicId).ToArray();
                if (versionIds.Any(value => !PublicIdFormat.IsValid(value, PublicIdPrefixes.AccessProfileVersionPrefix)))
                    return Results.BadRequest(new ApiErrorResponse("invalid_assignments"));
                var versions = await database.AccessProfileVersions.AsNoTracking()
                    .Where(item => versionIds.Contains(item.PublicId!))
                    .ToDictionaryAsync(item => item.PublicId!, item => item.Id, cancellationToken);
                if (versions.Count != versionIds.Distinct(StringComparer.Ordinal).Count())
                    return Results.BadRequest(new ApiErrorResponse("invalid_assignments"));
                var contexts = await database.CameraContexts.AsNoTracking()
                    .ToDictionaryAsync(context => context.PublicId!, context => context.ContextId, cancellationToken);
                var contextPublicIds = contexts.ToDictionary(item => item.Value, item => item.Key,
                    StringComparer.Ordinal);
                var cameraRows = await database.CameraCatalog.AsNoTracking()
                    .Select(camera => new { camera.PublicId, camera.ContextId, camera.CameraId })
                    .ToArrayAsync(cancellationToken);
                var cameras = cameraRows.ToDictionary(camera => camera.PublicId!,
                    camera => (camera.ContextId, camera.CameraId, ContextPublicId: contextPublicIds[camera.ContextId]));
                var modules = PermissionCatalog.All.Select(item => item.Module)
                    .ToHashSet(StringComparer.Ordinal);
                PermissionScope MapScope(PublicScopeRequest scope)
                {
                    var kind = scope.Kind?.Trim().ToLowerInvariant() ??
                        (scope.ModuleId is not null ? "module"
                            : scope.ContextId is not null && scope.CameraId is not null ? "camera"
                            : scope.ContextId is not null ? "context" : "global");
                    return kind switch
                    {
                        "global" when scope.ContextId is null && scope.CameraId is null && scope.ModuleId is null =>
                            PermissionScope.Global,
                        "module" when scope.ContextId is null && scope.CameraId is null &&
                                      !string.IsNullOrWhiteSpace(scope.ModuleId) && modules.Contains(scope.ModuleId) =>
                            PermissionScope.ForModule(scope.ModuleId),
                        "context" when scope.CameraId is null && scope.ModuleId is null &&
                                       scope.ContextId is not null &&
                                       PublicIdFormat.IsValid(scope.ContextId, PublicIdPrefixes.ContextPrefix) &&
                                       contexts.TryGetValue(scope.ContextId, out var contextId) =>
                            PermissionScope.ForContext(contextId),
                        "camera" when scope.ModuleId is null && scope.ContextId is not null &&
                                      scope.CameraId is not null &&
                                      PublicIdFormat.IsValid(scope.ContextId, PublicIdPrefixes.ContextPrefix) &&
                                      PublicIdFormat.IsValid(scope.CameraId, PublicIdPrefixes.CameraPrefix) &&
                                      cameras.TryGetValue(scope.CameraId, out var camera) &&
                                      camera.ContextPublicId == scope.ContextId =>
                            PermissionScope.ForCamera(camera.ContextId, camera.CameraId),
                        _ => throw new ArgumentException("Escopo inválido.")
                    };
                }
                var internalAssignments = request.Assignments.Select(item => new ProfileAssignmentRequest(
                    versions[item.ProfileVersionPublicId], item.Scopes.Select(MapScope).ToArray())).ToArray();
                await profiles.ReplaceAssignmentsAsync(actor.Id, targetId.Value, internalAssignments, cancellationToken);
                return Results.NoContent();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                              KeyNotFoundException or UnauthorizedAccessException)
            {
                return EndpointSecurity.AdminIdentityError(exception);
            }
        }).RequireAuthorization(PermissionPolicy.Name("admin.users.manage"))
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
