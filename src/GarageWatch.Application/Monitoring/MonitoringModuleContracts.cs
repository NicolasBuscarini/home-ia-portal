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

public interface IMonitoringModuleClient
{
    Task<MonitoringModuleContract> GetContractAsync(CancellationToken cancellationToken = default);

    Task<MonitoringCameraStatus> GetCameraStatusAsync(string cameraId,
        CancellationToken cancellationToken = default);
}
