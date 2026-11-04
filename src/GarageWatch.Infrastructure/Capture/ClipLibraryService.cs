using System.Security.Cryptography;
using GarageWatch.Application.Authorization;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Capture;

public sealed record ClipListItem(string PublicId, string CameraId, DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc, string State, string ArchiveStatus, string AnalysisStatus, string? ThumbnailUrl,
    long? SizeBytes, string? TriggerReason, string? TriggerConfigVersion, bool IsPreserved,
    long PreservationVersion);
public sealed record ClipPage(IReadOnlyList<ClipListItem> Items, int Page, int PageSize, int Total);
public sealed record ClipDetails(ClipListItem Clip, string? MediaUrl, string? MediaSha256,
    string? ThumbnailSha256, int AttemptCount,
    DateTimeOffset UpdatedAtUtc);
public sealed record ClipReadResult(ClipDetails? Details, string? MediaPath, string? ThumbnailPath,
    bool RemoteMedia = false, bool RemoteThumbnail = false);

public sealed class ClipLibraryService(IamDbContext database, AccessProfileService profiles,
    IMonitoringClipsClient? remoteClips = null,
    IMonitoringAnalysisClient? remoteAnalysis = null, IClipRetentionGateway? retention = null)
{
    // Listagem, detalhe e mídia vêm sempre do serviço de Monitoramento (ADR108); o portal
    // mantém apenas autorização, projeção de câmeras e auditoria.
    private IMonitoringClipsClient RemoteClipsClient => remoteClips ??
        throw new InvalidOperationException("monitoring_clips_unavailable");

    public async Task<ClipPage> ListAsync(Guid userId, bool isAdministrator, string? cameraId,
        string? publicId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? state,
        int? minDurationSeconds, int? maxDurationSeconds, string? analysis, bool? preserved, string? sort,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page is < 1 or > 100_000 || pageSize is < 1 or > 100 ||
            (fromUtc.HasValue && toUtc.HasValue && fromUtc > toUtc) ||
            (cameraId is not null && !PublicIdFormat.IsValid(cameraId, PublicIdPrefixes.CameraPrefix)) ||
            (publicId is not null && !PublicIdFormat.IsValid(publicId, PublicIdPrefixes.ClipPrefix)) ||
            (minDurationSeconds is < 0 or > 86_400) || (maxDurationSeconds is < 0 or > 86_400) ||
            (minDurationSeconds.HasValue && maxDurationSeconds.HasValue &&
                minDurationSeconds > maxDurationSeconds) ||
            (analysis is not null && analysis is not ("performed" or "not_performed")) ||
            (sort is not null && sort is not ("newest" or "oldest" or "longest" or "shortest" or
                "largest" or "smallest")) ||
            (state is not null && state is not ("collecting" or "ready" or "failed" or "lost_capacity")))
            throw new ArgumentException("invalid_clip_query");

        var allowedCameras = await AllowedCamerasAsync(userId, isAdministrator, cameraId, cancellationToken);
        if (cameraId is not null && allowedCameras.Count == 0) return new([], page, pageSize, 0);

        var accessible = allowedCameras.Select(camera => camera.CameraId).ToHashSet(StringComparer.Ordinal);
        var remote = await RemoteClipsClient.ListAsync(new MonitoringClipQuery(accessible.ToArray(), publicId,
            fromUtc, toUtc, state, minDurationSeconds, maxDurationSeconds, analysis, preserved, sort, page, pageSize),
            cancellationToken);
        if (remote is null) throw new InvalidOperationException("monitoring_clips_unavailable");
        var preservation = await PreservationStatesAsync(remote.Items.Select(item => item.PublicId).ToArray(),
            cancellationToken);
        var jobs = await RemoteAnalysisStatesAsync(remote.Items, cancellationToken);
        var publicIds = allowedCameras.ToDictionary(camera => camera.CameraId, camera => camera.PublicId!,
            StringComparer.Ordinal);
        var items = remote.Items.Select(item => MapRemote(item,
            jobs is null ? "unavailable" : jobs.GetValueOrDefault(item.PublicId) ?? "not_requested",
            publicIds.GetValueOrDefault(item.CameraId, item.CameraId),
            preservation.GetValueOrDefault(item.PublicId))).ToArray();
        return new ClipPage(items, remote.Page, remote.PageSize, remote.Total);
    }

    public async Task<ClipReadResult> GetAsync(Guid userId, bool isAdministrator, string publicId,
        CancellationToken cancellationToken)
    {
        if (!PublicIdFormat.IsValid(publicId, PublicIdPrefixes.ClipPrefix)) return new(null, null, null);
        var remote = await RemoteClipsClient.GetAsync(publicId, cancellationToken);
        if (remote is null) return new(null, null, null);
        var camera = await AuthorizedCameraAsync(userId, isAdministrator, remote.CameraId, cancellationToken);
        if (camera is null) return new(null, null, null);
        var states = await RemoteAnalysisStatesAsync([remote], cancellationToken);
        var preservation = await PreservationStatesAsync([remote.PublicId], cancellationToken);
        var item = MapRemote(remote,
            states is null ? "unavailable" : states.GetValueOrDefault(remote.PublicId) ?? "not_requested",
            camera.PublicId!, preservation.GetValueOrDefault(remote.PublicId));
        return new ClipReadResult(new ClipDetails(item, remote.SizeBytes is null
                ? null
                : $"/api/v1/clips/{publicId}/media", remote.MediaSha256, remote.ThumbnailSha256,
            remote.AttemptCount, remote.UpdatedAtUtc), null, null, true, true);
    }

    public static async Task<bool> HasValidHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit) || !File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(expected));
    }

    private async Task<List<CameraProjection>> AllowedCamerasAsync(Guid userId, bool isAdministrator,
        string? cameraPublicId, CancellationToken cancellationToken)
    {
        var candidates = await database.CameraCatalog.AsNoTracking()
            .Where(camera => camera.IsEnabled && camera.Context.IsEnabled &&
                (cameraPublicId == null || camera.PublicId == cameraPublicId))
            .Select(camera => new { camera.CameraId, camera.PublicId, camera.ContextId })
            .ToArrayAsync(cancellationToken);
        var allowed = new List<CameraProjection>();
        foreach (var camera in candidates)
            if (isAdministrator || await profiles.IsAllowedAsync(userId,
                    new PermissionRequest("clips.read", camera.ContextId, camera.CameraId), cancellationToken))
                allowed.Add(new CameraProjection(camera.CameraId, camera.PublicId, camera.ContextId));
        return allowed;
    }

    private async Task<CameraProjection?> AuthorizedCameraAsync(Guid userId, bool isAdministrator,
        string internalCameraId, CancellationToken cancellationToken)
    {
        var camera = await database.CameraCatalog.AsNoTracking()
            .Where(item => item.CameraId == internalCameraId && item.IsEnabled && item.Context.IsEnabled)
            .Select(item => new { item.CameraId, item.PublicId, item.ContextId })
            .SingleOrDefaultAsync(cancellationToken);
        if (camera is null) return null;
        if (isAdministrator || await profiles.IsAllowedAsync(userId,
                new PermissionRequest("clips.read", camera.ContextId, camera.CameraId), cancellationToken))
            return new CameraProjection(camera.CameraId, camera.PublicId, camera.ContextId);
        return null;
    }

    private async Task<Dictionary<string, (bool IsPreserved, long Version)>> PreservationStatesAsync(
        string[] clipPublicIds, CancellationToken cancellationToken)
    {
        if (clipPublicIds.Length == 0 || retention is null) return new(StringComparer.Ordinal);
        try
        {
            return await retention.GetPreservationStatesAsync(clipPublicIds, cancellationToken);
        }
        catch (MonitoringRetentionUnavailableException)
        {
            // Preservação é enriquecimento: uma indisponibilidade do serviço não esconde o clipe.
            return new(StringComparer.Ordinal);
        }
    }

    // A indisponibilidade da análise é um estado degradado do módulo, não um erro da listagem:
    // o clipe continua legível e recebe o estado "unavailable" até o serviço responder de novo.
    private async Task<Dictionary<string, string>?> RemoteAnalysisStatesAsync(
        IReadOnlyCollection<MonitoringClipItem> clips, CancellationToken cancellationToken)
    {
        if (remoteAnalysis is null) return new(StringComparer.Ordinal);
        try
        {
            var jobs = await remoteAnalysis.LatestForClipsAsync(clips.Select(clip => clip.PublicId).ToArray(),
                cancellationToken);
            return jobs?.ToDictionary(job => job.ClipPublicId, job => job.State, StringComparer.Ordinal);
        }
        catch (MonitoringAnalysisUnavailableException)
        {
            return null;
        }
    }

    private static ClipListItem MapRemote(MonitoringClipItem clip, string analysisStatus, string cameraPublicId,
        (bool IsPreserved, long Version) preservation = default) =>
        new(clip.PublicId, cameraPublicId, clip.StartedAtUtc, clip.EndedAtUtc, clip.State, clip.ArchiveStatus,
            analysisStatus, clip.ThumbnailSha256 is null ? null : $"/api/v1/clips/{clip.PublicId}/thumbnail",
            clip.SizeBytes, clip.TriggerReason, clip.TriggerConfigVersion, preservation.IsPreserved,
            preservation.Version);

    private sealed record CameraProjection(string CameraId, string? PublicId, string ContextId);
}
