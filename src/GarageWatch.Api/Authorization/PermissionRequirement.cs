using System.Security.Claims;
using GarageWatch.Application.Authorization;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;

namespace GarageWatch.Api.Authorization;

public sealed record PermissionRequirement(string Key) : IAuthorizationRequirement;

/// <summary>
/// Evaluates one catalog permission against the profile-derived subject of the current session.
/// The requirement carries only the key; the target module comes from the catalog definition, and
/// ownership remains an explicit capability instead of an invisible role bypass.
/// </summary>
public sealed class PermissionAuthorizationHandler(AccessProfileService profiles)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;
        if (!PermissionCatalog.TryGet(requirement.Key, out var definition)) return;
        var rawUserId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(rawUserId, out var userId)) return;
        var request = new PermissionRequest(requirement.Key, ModuleId: definition.Module);
        if (await profiles.IsAllowedAsync(userId, request, CancellationToken.None))
            context.Succeed(requirement);
    }
}
