namespace GarageWatch.Application.Monitoring;

public sealed record MonitoringModuleContract(
    string SchemaVersion,
    string ModuleId,
    string ContractVersion,
    string ExecutionMode,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset? GeneratedAtUtc,
    bool Reachable,
    string? ErrorCode);

public sealed record MonitoringCameraStatus(
    string SchemaVersion,
    string ModuleId,
    string CameraId,
    string Status,
    string? StreamEpoch,
    long Segments,
    long Gaps,
    DateTimeOffset? LastSegmentEndedAtUtc,
    string? ReasonCode,
    bool Reachable,
    string? ErrorCode);

public sealed record MonitoringCaptureSnapshot(
    string SchemaVersion,
    string Profile,
    string CameraId,
    bool Enabled,
    int SegmentSeconds,
    int MaximumSegments,
    long MaximumSegmentBytes,
    long Revision,
    string State,
    string Adapter,
    string? LastErrorCode,
    long Gaps,
    int Segments);

public sealed record MonitoringCaptureSettings(
    bool Enabled,
    int SegmentSeconds,
    int MaximumSegments,
    long MaximumSegmentBytes,
    long Revision);

public interface IMonitoringModuleClient
{
    Task<MonitoringModuleContract> GetContractAsync(CancellationToken cancellationToken = default);

    Task<MonitoringCameraStatus> GetCameraStatusAsync(string cameraId,
        CancellationToken cancellationToken = default);
}

public interface IMonitoringCaptureClient
{
    Task<MonitoringCaptureSnapshot?> GetCaptureAsync(CancellationToken cancellationToken = default);

    Task<MonitoringCaptureSettings?> UpdateCaptureAsync(MonitoringCaptureSettings settings,
        CancellationToken cancellationToken = default);
}
