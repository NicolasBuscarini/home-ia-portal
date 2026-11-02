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

public interface IMonitoringModuleClient
{
    Task<MonitoringModuleContract> GetContractAsync(CancellationToken cancellationToken = default);
}
