using GarageWatch.Application.Identity;
using GarageWatch.Application.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public interface IClipRetentionGateway
{
    Task<ClipRetentionSettings> GetSettingsAsync(string publicCameraId,
        CancellationToken cancellationToken);

    Task<ClipRetentionSettings> UpdateSettingsAsync(Guid actorId, string publicCameraId, long expectedVersion,
        int retentionDays, int? maximumStorageGigabytes, CancellationToken cancellationToken);

    Task<ClipPreservationSettings> SetPreservationAsync(Guid actorId, string publicClipId, long expectedVersion,
        bool isPreserved, CancellationToken cancellationToken);

    Task<Dictionary<string, (bool IsPreserved, long Version)>> GetPreservationStatesAsync(
        IReadOnlyCollection<string> clipPublicIds, CancellationToken cancellationToken);
}

public sealed class LocalClipRetentionGateway(ClipRetentionService service, IamDbContext database)
    : IClipRetentionGateway
{
    public Task<ClipRetentionSettings> GetSettingsAsync(string publicCameraId,
        CancellationToken cancellationToken) => service.GetSettingsAsync(publicCameraId, cancellationToken);

    public Task<ClipRetentionSettings> UpdateSettingsAsync(Guid actorId, string publicCameraId,
        long expectedVersion, int retentionDays, int? maximumStorageGigabytes,
        CancellationToken cancellationToken) => service.UpdateSettingsAsync(actorId, publicCameraId, expectedVersion,
        retentionDays, maximumStorageGigabytes, cancellationToken);

    public Task<ClipPreservationSettings> SetPreservationAsync(Guid actorId, string publicClipId,
        long expectedVersion, bool isPreserved, CancellationToken cancellationToken) =>
        service.SetPreservationAsync(actorId, publicClipId, expectedVersion, isPreserved, cancellationToken);

    public async Task<Dictionary<string, (bool IsPreserved, long Version)>> GetPreservationStatesAsync(
        IReadOnlyCollection<string> clipPublicIds, CancellationToken cancellationToken)
    {
        if (clipPublicIds.Count == 0) return new(StringComparer.Ordinal);
        return await database.ClipPreservations.AsNoTracking()
            .Where(item => clipPublicIds.Contains(item.PublicId))
            .ToDictionaryAsync(item => item.PublicId, item => (item.IsPreserved, item.Version),
                cancellationToken);
    }
}

/// <summary>
/// Keeps identity, camera-catalog authorization and IAM audit in the portal while the monitoring
/// service owns the versioned retention policy, preservation state and archive execution.
/// </summary>
public sealed class RemoteClipRetentionGateway(IamDbContext database, IMonitoringRetentionClient client,
    IIamUnitOfWork unitOfWork, IPlatformEventRepository auditEvents, TimeProvider timeProvider)
    : IClipRetentionGateway
{
    public async Task<ClipRetentionSettings> GetSettingsAsync(string publicCameraId,
        CancellationToken cancellationToken)
    {
        var cameraId = await ResolveCameraAsync(publicCameraId, cancellationToken);
        var policy = await client.GetPolicyAsync(cameraId, cancellationToken) ?? throw new KeyNotFoundException();
        return Map(publicCameraId, policy);
    }

    public async Task<ClipRetentionSettings> UpdateSettingsAsync(Guid actorId, string publicCameraId,
        long expectedVersion, int retentionDays, int? maximumStorageGigabytes,
        CancellationToken cancellationToken)
    {
        var cameraId = await ResolveCameraAsync(publicCameraId, cancellationToken);
        MonitoringRetentionPolicy policy;
        try
        {
            policy = await client.UpdatePolicyAsync(cameraId, expectedVersion, retentionDays,
                maximumStorageGigabytes, cancellationToken);
        }
        catch (MonitoringRetentionRejectedException exception)
        {
            throw new ClipRetentionConflictException(exception.Message);
        }
        await AuditAsync(actorId, "clip.retention.policy.updated", publicCameraId, cancellationToken);
        return Map(publicCameraId, policy);
    }

    public async Task<ClipPreservationSettings> SetPreservationAsync(Guid actorId, string publicClipId,
        long expectedVersion, bool isPreserved, CancellationToken cancellationToken)
    {
        if (!PublicIdFormat.IsValid(publicClipId, PublicIdPrefixes.ClipPrefix) || expectedVersion < 0)
            throw new ArgumentException("invalid_clip_preservation");
        MonitoringPreservation result;
        try
        {
            result = await client.SetPreservationAsync(publicClipId, expectedVersion, isPreserved,
                cancellationToken);
        }
        catch (MonitoringRetentionRejectedException exception)
        {
            throw new ClipRetentionConflictException(exception.Message);
        }
        await AuditAsync(actorId, isPreserved ? "clip.preservation.enabled" : "clip.preservation.disabled",
            publicClipId, cancellationToken);
        return new ClipPreservationSettings(result.ClipPublicId, result.IsPreserved, result.Version);
    }

    public async Task<Dictionary<string, (bool IsPreserved, long Version)>> GetPreservationStatesAsync(
        IReadOnlyCollection<string> clipPublicIds, CancellationToken cancellationToken)
    {
        if (clipPublicIds.Count == 0) return new(StringComparer.Ordinal);
        if (clipPublicIds.Any(id => !PublicIdFormat.IsValid(id, PublicIdPrefixes.ClipPrefix)))
            throw new ArgumentException("invalid_clip_preservation_query");
        var states = await client.ListPreservationsAsync(clipPublicIds, cancellationToken);
        return states.ToDictionary(item => item.ClipPublicId,
            item => (item.IsPreserved, item.Version), StringComparer.Ordinal);
    }

    private async Task<string> ResolveCameraAsync(string publicCameraId, CancellationToken cancellationToken)
    {
        if (!PublicIdFormat.IsValid(publicCameraId, PublicIdPrefixes.CameraPrefix))
            throw new KeyNotFoundException();
        return await database.CameraCatalog.AsNoTracking()
                   .Where(item => item.PublicId == publicCameraId)
                   .Select(item => item.CameraId).SingleOrDefaultAsync(cancellationToken)
               ?? throw new KeyNotFoundException();
    }

    private async Task AuditAsync(Guid actorId, string action, string resourceId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await unitOfWork.ExecuteAtomicallyAsync(token =>
        {
            auditEvents.Append(actorId, null, action, resourceId, now);
            return Task.FromResult(0);
        }, cancellationToken);
    }

    private static ClipRetentionSettings Map(string publicCameraId, MonitoringRetentionPolicy policy) =>
        new(publicCameraId, policy.RetentionDays, policy.MaximumStorageGigabytes, policy.Version,
            policy.UpdatedAtUtc);
}
