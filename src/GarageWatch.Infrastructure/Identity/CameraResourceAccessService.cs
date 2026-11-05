using GarageWatch.Application.Cameras;
using GarageWatch.Application.Authorization;
using GarageWatch.Application.Identity;
using GarageWatch.Application.PlatformEvents;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public sealed class CameraResourceAccessService(
    IamDbContext database,
    IIamUnitOfWork unitOfWork,
    IPlatformEventRecorder recorder,
    AccessProfileService profiles) : ICameraResourceAccess
{
    public async Task<bool> CanReadLiveAsync(Guid userId, string cameraId,
        CancellationToken cancellationToken = default)
    {
        var catalogCamera = await database.CameraCatalog.AsNoTracking()
            .Where(camera => camera.PublicId == cameraId && camera.IsEnabled && camera.Context.IsEnabled)
            .Select(camera => new { camera.CameraId, camera.ContextId })
            .SingleOrDefaultAsync(cancellationToken);

        var allowed = catalogCamera is not null && await profiles.IsAllowedAsync(userId,
            new PermissionRequest("live.video.view", catalogCamera.ContextId, catalogCamera.CameraId), cancellationToken);
        if (allowed) return true;

        await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            var subject = await database.FindUserPublicIdAsync(userId, token);
            var resource = PublicIdFormat.IsValid(cameraId, PublicIdPrefixes.CameraPrefix) ? cameraId : null;
            await recorder.RecordRequiredAsync(new PlatformEventDraft(
                "camera.access.denied", PlatformEventOutcomes.Denied,
                Subject: subject is null ? null : new PlatformEventReferenceV1("user", subject),
                Resource: resource is null ? null : new PlatformEventReferenceV1("camera", resource)), token);
        }, cancellationToken);
        return false;
    }

    public async Task<bool> HasAnyLiveAccessAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var cameras = await database.CameraCatalog.AsNoTracking()
            .Where(camera => camera.IsEnabled && camera.Context.IsEnabled)
            .Select(camera => new { camera.ContextId, camera.CameraId }).ToArrayAsync(cancellationToken);
        foreach (var camera in cameras)
            if (await profiles.IsAllowedAsync(userId,
                    new PermissionRequest("live.video.view", camera.ContextId, camera.CameraId), cancellationToken))
                return true;
        return false;
    }

    private static bool IsSafeIdentifier(string value) => value.Length is > 0 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
