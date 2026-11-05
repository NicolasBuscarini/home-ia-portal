using System.Security.Cryptography;
using System.Text.Json;
using GarageWatch.Application.Authorization;
using GarageWatch.Application.Identity;
using GarageWatch.Application.Monitoring;
using GarageWatch.Application.PlatformEvents;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public sealed record ClipAnalysisPolicyResult(string CameraId, string ContextId, string Policy, long Version,
    DateTimeOffset UpdatedAtUtc);
public sealed record ClipAnalysisPolicyView(string CameraId, string ContextId, string Policy, long Version,
    DateTimeOffset? UpdatedAtUtc, bool CanRequest, bool CanManage);
public sealed record ClipAnalysisJobResult(string PublicId, string ClipPublicId, string AnalysisVersion, string State,
    bool Existing);
public sealed class ClipAnalysisConflictException(string code) : Exception(code);
public sealed class ClipAnalysisDeniedException(string code) : Exception(code);
public sealed class ClipAnalysisCapacityException : Exception;

public sealed class ClipAnalysisDispatchService(IamDbContext database, IIamUnitOfWork unitOfWork,
    IPlatformEventRecorder recorder, AccessProfileService profiles, CaptureOptions capture,
    ClipAnalysisDispatchOptions options,
    IMonitoringAnalysisClient? remoteAnalysis = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // A análise é sempre executada pelo serviço de Monitoramento (ADR108); o portal mantém
    // política, autorização, janela e auditoria.
    private IMonitoringAnalysisClient RemoteClient => remoteAnalysis ??
        throw new MonitoringAnalysisUnavailableException();

    public async Task<ClipAnalysisPolicyView> GetPolicyAsync(Guid actorId, string cameraId,
        CancellationToken cancellationToken)
    {
        if (!PublicIdFormat.IsValid(cameraId, PublicIdPrefixes.CameraPrefix)) throw new KeyNotFoundException();
        var scope = await FindEnabledCameraByPublicIdAsync(cameraId, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (!await profiles.IsAllowedAsync(actorId,
                new PermissionRequest("clips.read", scope.ContextId, scope.CameraId), cancellationToken))
            throw new KeyNotFoundException();
        var canRequest = await profiles.IsAllowedAsync(actorId,
            new PermissionRequest("clips.analysis.request", scope.ContextId, scope.CameraId), cancellationToken);
        var canManage = await profiles.IsAllowedAsync(actorId,
            new PermissionRequest("clips.analysis.policy.manage", scope.ContextId, scope.CameraId), cancellationToken);
        var remote = await RemoteClient.GetPolicyAsync(scope.CameraId, cancellationToken);
        return remote is null
            ? new(scope.CameraPublicId, scope.ContextPublicId, ClipAnalysisPolicies.ManualOnly, 0,
                DateTimeOffset.UnixEpoch, canRequest, canManage)
            : new(scope.CameraPublicId, scope.ContextPublicId, remote.Policy, remote.Version,
                remote.UpdatedAtUtc, canRequest, canManage);
    }

    public async Task<ClipAnalysisPolicyResult> UpdatePolicyAsync(Guid actorId, string cameraId, long expectedVersion,
        string policy, CancellationToken cancellationToken)
    {
        if (expectedVersion < 0 || !ClipAnalysisPolicies.IsValid(policy))
            throw new ArgumentException("Política ou versão inválida.");

        if (!PublicIdFormat.IsValid(cameraId, PublicIdPrefixes.CameraPrefix)) throw new KeyNotFoundException();
        var scope = await FindEnabledCameraByPublicIdAsync(cameraId, cancellationToken) ?? throw new KeyNotFoundException();
        if (!await profiles.IsAllowedAsync(actorId, new PermissionRequest("clips.analysis.policy.manage", scope.ContextId,
                scope.CameraId), cancellationToken)) throw new UnauthorizedAccessException();

        MonitoringAnalysisPolicy updated;
        try
        {
            updated = await RemoteClient.UpdatePolicyAsync(scope.CameraId, scope.ContextId, expectedVersion,
                policy, actorId.ToString("D"), cancellationToken) ??
                throw new MonitoringAnalysisUnavailableException();
        }
        catch (MonitoringAnalysisConflictException)
        {
            throw new ClipAnalysisConflictException("policy_version_conflict");
        }
        await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            await AuditAsync(actorId, "clip.analysis.policy.updated", cameraId, null, token);
        }, cancellationToken);
        return new ClipAnalysisPolicyResult(scope.CameraPublicId, scope.ContextPublicId, updated.Policy,
            updated.Version, updated.UpdatedAtUtc);
    }

    public async Task<ClipAnalysisJobResult> RequestAsync(Guid actorId, string clipId, Guid idempotencyKey,
        string? requestedAnalysisVersion, CancellationToken cancellationToken)
    {
        if (idempotencyKey == Guid.Empty || !PublicIdFormat.IsValid(clipId, PublicIdPrefixes.ClipPrefix) ||
            (requestedAnalysisVersion is not null && !ClipAnalysisDispatchOptions.IsSafeVersion(requestedAnalysisVersion)))
            throw new ArgumentException("Pedido de análise inválido.");
        var clip = await ReadReadyClipByPublicIdAsync(clipId, cancellationToken) ?? throw new KeyNotFoundException();
        var scope = await FindEnabledCameraAsync(clip.CameraId, cancellationToken) ?? throw new KeyNotFoundException();
        if (!await profiles.IsAllowedAsync(actorId, new PermissionRequest("clips.analysis.request", scope.ContextId,
                scope.CameraId), cancellationToken))
        {
            await AuditDeniedAsync(actorId, clip.ClipId, cancellationToken);
            throw new UnauthorizedAccessException();
        }
        var analysisVersion = requestedAnalysisVersion ?? options.DefaultAnalysisVersion;
        try
        {
            var job = await RemoteClient.EnqueueAsync(new MonitoringAnalysisEnqueue(clip.ClipId, clip.PublicId!,
                    scope.CameraId, scope.ContextId, idempotencyKey, analysisVersion, "manual"),
                cancellationToken) ?? throw new MonitoringAnalysisUnavailableException();
            if (!job.Existing)
                await unitOfWork.ExecuteAtomicallyAsync(async token =>
                    await AuditAsync(actorId, "clip.analysis.request.accepted", clipId, null, token),
                    cancellationToken);
            return new ClipAnalysisJobResult(job.PublicId, job.ClipPublicId, job.AnalysisVersion, job.State,
                job.Existing);
        }
        catch (MonitoringAnalysisConflictException exception)
        {
            throw new ClipAnalysisConflictException(exception.Message);
        }
        catch (MonitoringAnalysisCapacityException)
        {
            throw new ClipAnalysisCapacityException();
        }
        catch (MonitoringAnalysisDeniedException exception)
        {
            await AuditDeniedAsync(actorId, clip.ClipId, cancellationToken);
            throw new ClipAnalysisDeniedException(exception.Message);
        }
    }

    public async Task AutoRequestAsync(ClipManifestV1 clip, CancellationToken cancellationToken)
    {
        if (clip.State != "ready" || !await IsMediaIntactAsync(clip, cancellationToken)) return;
        var scope = await FindEnabledCameraAsync(clip.CameraId, cancellationToken);
        if (scope is null) return;
        var effectivePolicy = (await RemoteClient.GetPolicyAsync(scope.CameraId, cancellationToken))?.Policy;
        if (effectivePolicy is null or ClipAnalysisPolicies.ManualOnly or ClipAnalysisPolicies.Never) return;
        if (effectivePolicy == ClipAnalysisPolicies.LargeMotion && !HasVersionedLargeMotionEvidence(clip)) return;
        try
        {
            await RemoteClient.EnqueueAsync(new MonitoringAnalysisEnqueue(clip.ClipId, clip.PublicId!,
                scope.CameraId, scope.ContextId, DeterministicKey(clip.ClipId, options.DefaultAnalysisVersion),
                options.DefaultAnalysisVersion, "automatic"), cancellationToken);
        }
        catch (Exception exception) when (exception is ClipAnalysisCapacityException or ClipAnalysisDeniedException
            or MonitoringAnalysisCapacityException or MonitoringAnalysisDeniedException
            or MonitoringAnalysisUnavailableException)
        {
            // Dispatch failures are intentionally isolated from capture and clip assembly.
        }
    }

    private async Task AuditDeniedAsync(Guid actorId, string clipId, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            await AuditAsync(actorId, "clip.analysis.request.denied", clipId, PlatformEventOutcomes.Denied, token);
        }, cancellationToken);

    private async Task<CameraScope?> FindEnabledCameraAsync(string cameraId, CancellationToken cancellationToken) =>
        await database.CameraCatalog.AsNoTracking().Where(camera => camera.CameraId == cameraId && camera.IsEnabled &&
            camera.Context.IsEnabled).Select(camera => new CameraScope(camera.ContextId, camera.CameraId,
                camera.PublicId!, camera.Context.PublicId!))
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<CameraScope?> FindEnabledCameraByPublicIdAsync(string cameraPublicId,
        CancellationToken cancellationToken) =>
        await database.CameraCatalog.AsNoTracking().Where(camera => camera.PublicId == cameraPublicId &&
            camera.IsEnabled && camera.Context.IsEnabled).Select(camera => new CameraScope(camera.ContextId,
            camera.CameraId, camera.PublicId!, camera.Context.PublicId!)).SingleOrDefaultAsync(cancellationToken);

    private async Task<ClipManifestV1?> ReadReadyClipByPublicIdAsync(string publicId, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(capture.SpoolDirectory, "clips", "manifests");
        if (!Directory.Exists(directory)) return null;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Take(10_000))
        {
            var candidate = await ReadReadyClipByPathAsync(path, cancellationToken);
            if (candidate?.PublicId == publicId) return candidate;
        }
        return null;
    }

    private async Task<ClipManifestV1?> ReadReadyClipByPathAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var clip = await JsonSerializer.DeserializeAsync<ClipManifestV1>(stream, JsonOptions, cancellationToken);
            return clip is { SchemaVersion: "1.0", State: "ready" } &&
                   PublicIdFormat.IsValid(clip.PublicId, PublicIdPrefixes.ClipPrefix) &&
                   await IsMediaIntactAsync(clip, cancellationToken) ? clip : null;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private async Task<bool> IsMediaIntactAsync(ClipManifestV1 clip, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clip.MediaFileName) || string.IsNullOrWhiteSpace(clip.MediaSha256) ||
            Path.GetFileName(clip.MediaFileName) != clip.MediaFileName) return false;
        var path = Path.Combine(capture.SpoolDirectory, "clips", "media", clip.MediaFileName);
        if (!File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        if (clip.MediaSha256.Length != 64 || !clip.MediaSha256.All(Uri.IsHexDigit)) return false;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(clip.MediaSha256));
    }

    private static bool HasVersionedLargeMotionEvidence(ClipManifestV1 clip) => false;
    private static Guid DeterministicKey(string clipId, string analysisVersion) =>
        new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{clipId}:{analysisVersion}"))[..16]);
    private async Task AuditAsync(Guid? actorId, string action, string resourceId, string? outcome,
        CancellationToken cancellationToken)
    {
        var subject = actorId is null ? null : await database.FindUserPublicIdAsync(actorId.Value, cancellationToken);
        var resourceType = action.StartsWith("clip.analysis.policy", StringComparison.Ordinal) ? "camera" : "clip";
        await recorder.RecordRequiredAsync(new PlatformEventDraft(
            action, outcome ?? PlatformEventOutcomes.Success,
            Subject: subject is null ? null : new PlatformEventReferenceV1("user", subject),
            Resource: new PlatformEventReferenceV1(resourceType, resourceId)), cancellationToken);
    }
    private sealed record CameraScope(string ContextId, string CameraId, string CameraPublicId, string ContextPublicId);
}
