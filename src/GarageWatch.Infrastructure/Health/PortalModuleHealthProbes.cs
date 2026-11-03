using GarageWatch.Application.Monitoring;
using GarageWatch.Application.Portal;

namespace GarageWatch.Infrastructure.Health;

public sealed class MonitoringModuleHealthProbe(IMonitoringModuleClient monitoring) : IPortalModuleHealthProbe
{
    public string ModuleId => "monitoring";

    public string Kind => PortalModuleKinds.Module;

    public async Task<PortalModuleHealthProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var contract = await monitoring.GetContractAsync(cancellationToken);
        return new PortalModuleHealthProbeResult(contract.Reachable, contract.ErrorCode, contract.Capabilities);
    }
}

public sealed class ActivityWatchHealthProbe(IActivityWatchModule activityWatch) : IPortalModuleHealthProbe
{
    public string ModuleId => "activitywatch";

    public string Kind => PortalModuleKinds.Integration;

    public async Task<PortalModuleHealthProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var health = await activityWatch.CheckHealthAsync(cancellationToken);
        return new PortalModuleHealthProbeResult(health.Reachable, health.ErrorCode);
    }
}

/// <summary>
/// Module served by the portal process itself. The report is only produced while the process is
/// answering, so no extra probe is needed and no network call is made.
/// </summary>
public sealed class InProcessModuleHealthProbe(string moduleId) : IPortalModuleHealthProbe
{
    public string ModuleId => moduleId;

    public string Kind => PortalModuleKinds.Module;

    public Task<PortalModuleHealthProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new PortalModuleHealthProbeResult(true, null));
}
