namespace GarageWatch.Application.Monitoring;

public sealed record MonitoringAnalysisPolicy(string CameraId, string Policy, long Version,
    DateTimeOffset UpdatedAtUtc);

public sealed record MonitoringAnalysisEnqueue(string ClipId, string ClipPublicId, string CameraId,
    string ContextId, Guid IdempotencyKey, string AnalysisVersion, string RequestKind);

public sealed record MonitoringAnalysisJob(string PublicId, string ClipPublicId, string AnalysisVersion,
    string State, bool Existing);

public sealed record MonitoringAnalysisJobStatus(string ClipPublicId, string State, string? Outcome);

public sealed class MonitoringAnalysisConflictException(string code) : Exception(code);

public sealed class MonitoringAnalysisDeniedException(string code) : Exception(code);

public sealed class MonitoringAnalysisCapacityException : Exception;

public sealed class MonitoringAnalysisUnavailableException : Exception;

public interface IMonitoringAnalysisClient
{
    Task<MonitoringAnalysisPolicy?> GetPolicyAsync(string cameraId,
        CancellationToken cancellationToken = default);

    Task<MonitoringAnalysisPolicy?> UpdatePolicyAsync(string cameraId, string contextId, long expectedVersion,
        string policy, string actorId, CancellationToken cancellationToken = default);

    Task<MonitoringAnalysisJob?> EnqueueAsync(MonitoringAnalysisEnqueue request,
        CancellationToken cancellationToken = default);

    Task<MonitoringAnalysisJob?> ClaimAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MonitoringAnalysisJobStatus>?> LatestForClipsAsync(
        IReadOnlyCollection<string> clipPublicIds, CancellationToken cancellationToken = default);
}
