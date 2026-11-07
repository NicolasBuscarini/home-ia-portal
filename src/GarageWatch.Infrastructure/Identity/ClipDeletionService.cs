using GarageWatch.Application.Authorization;
using GarageWatch.Application.Identity;
using GarageWatch.Application.Monitoring;
using GarageWatch.Application.PlatformEvents;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public sealed record ClipDeletionItemResult(string ClipId, bool Deleted, string? Code = null);

/// <summary>
/// Authorizes and audits permanent deletion of clips. The monitoring service owns the media and
/// refuses preserved or archived clips; the portal never deletes spool files directly.
/// </summary>
public sealed class ClipDeletionService(IamDbContext database, IIamUnitOfWork unitOfWork,
    IPlatformEventRecorder recorder, AccessProfileService profiles,
    IMonitoringClipsClient clips)
{
    public async Task<IReadOnlyList<ClipDeletionItemResult>> DeleteAsync(Guid actorId,
        IReadOnlyCollection<string> clipIds, CancellationToken cancellationToken)
    {
        var results = new List<ClipDeletionItemResult>(clipIds.Count);
        foreach (var clipId in clipIds.Distinct(StringComparer.Ordinal))
        {
            var clip = PublicIdFormat.IsValid(clipId, PublicIdPrefixes.ClipPrefix)
                ? await clips.GetAsync(clipId, cancellationToken)
                : null;
            if (clip is null)
            {
                results.Add(new(clipId, false, "clip_not_found"));
                continue;
            }
            var scope = await database.CameraCatalog.AsNoTracking()
                .Where(camera => camera.CameraId == clip.CameraId && camera.IsEnabled &&
                    camera.Context.IsEnabled)
                .Select(camera => new { camera.ContextId, camera.CameraId })
                .SingleOrDefaultAsync(cancellationToken);
            if (scope is null)
            {
                results.Add(new(clipId, false, "clip_not_found"));
                continue;
            }
            if (!await profiles.IsAllowedAsync(actorId,
                    new PermissionRequest("clips.delete", scope.ContextId, scope.CameraId),
                    cancellationToken))
            {
                await AuditAsync(actorId, "clip.delete.denied", clipId,
                    PlatformEventOutcomes.Denied, cancellationToken);
                results.Add(new(clipId, false, "clip_not_found"));
                continue;
            }
            var deletion = await clips.DeleteAsync(clipId, cancellationToken);
            if (deletion.Status == MonitoringClipDeleteStatus.Deleted)
            {
                await AuditAsync(actorId, "clip.deleted", clipId, null, cancellationToken);
                results.Add(new(clipId, true));
            }
            else
            {
                results.Add(new(clipId, false, deletion.Error ?? "clip_delete_failed"));
            }
        }
        return results;
    }

    /// <summary>
    /// Administrator-only bulk deletion. The monitoring service evaluates each manifest against its
    /// preservation and archive state and reports aggregate counts; no clip identifiers are logged.
    /// </summary>
    public async Task<MonitoringClipDeleteAllResult?> DeleteAllAsync(Guid actorId,
        CancellationToken cancellationToken)
    {
        var result = await clips.DeleteAllAsync(cancellationToken);
        if (result is null) return null;
        await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            var subject = await database.FindUserPublicIdAsync(actorId, token);
            await recorder.RecordRequiredAsync(new PlatformEventDraft(
                "clip.delete.all", PlatformEventOutcomes.Success,
                Subject: subject is null ? null : new PlatformEventReferenceV1("user", subject),
                Metadata: new Dictionary<string, object?>
                {
                    ["deleted"] = result.Deleted,
                    ["skippedPreserved"] = result.SkippedPreserved,
                    ["skippedArchived"] = result.SkippedArchived,
                    ["skippedNotReady"] = result.SkippedNotReady,
                    ["skippedActiveAnalysis"] = result.SkippedActiveAnalysis,
                    ["failed"] = result.Failed
                }), token);
        }, cancellationToken);
        return result;
    }

    private async Task AuditAsync(Guid? actorId, string action, string resourceId, string? outcome,
        CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            var subject = actorId is null ? null : await database.FindUserPublicIdAsync(actorId.Value, token);
            await recorder.RecordRequiredAsync(new PlatformEventDraft(
                action, outcome ?? PlatformEventOutcomes.Success,
                Subject: subject is null ? null : new PlatformEventReferenceV1("user", subject),
                Resource: new PlatformEventReferenceV1("clip", resourceId)), token);
        }, cancellationToken);
}
