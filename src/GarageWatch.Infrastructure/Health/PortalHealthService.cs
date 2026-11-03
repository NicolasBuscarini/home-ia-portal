using System.Diagnostics;
using GarageWatch.Application.Portal;
using GarageWatch.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Health;

/// <summary>
/// Aggregates portal and module health from the persisted module catalog. Probes are project-owned
/// adapters, executed sequentially with each adapter's bounded timeout; a module without a known
/// probe is reported as <c>unknown</c> instead of being collapsed into a healthy result.
/// </summary>
public sealed class PortalHealthService(
    IamDbContext database,
    IEnumerable<IPortalModuleHealthProbe> probes,
    PortalOptions options,
    TimeProvider timeProvider) : IPortalHealth
{
    private const string SchemaVersion = "1.0";

    public async Task<PortalHealthReport> GetAsync(CancellationToken cancellationToken = default)
    {
        var modules = await database.PortalModules.AsNoTracking()
            .OrderBy(module => module.MenuGroup).ThenBy(module => module.MenuOrder).ThenBy(module => module.ModuleId)
            .ToArrayAsync(cancellationToken);
        var registry = probes.ToDictionary(probe => probe.ModuleId, StringComparer.Ordinal);
        var entries = new List<PortalModuleHealthEntry>(modules.Length);

        foreach (var module in modules)
        {
            var probe = registry.GetValueOrDefault(module.ModuleId);
            var kind = probe?.Kind ?? PortalModuleKinds.Module;
            if (!module.IsEnabled)
            {
                entries.Add(new PortalModuleHealthEntry(module.ModuleId, kind, PortalHealthStates.Disabled, false,
                    module.ContractVersion, 0, timeProvider.GetUtcNow(), null, []));
                continue;
            }

            if (probe is null)
            {
                entries.Add(new PortalModuleHealthEntry(module.ModuleId, kind, PortalHealthStates.Unknown, true,
                    module.ContractVersion, 0, timeProvider.GetUtcNow(), "health_probe_not_configured", []));
                continue;
            }

            var started = Stopwatch.GetTimestamp();
            PortalModuleHealthProbeResult result;
            try
            {
                result = await probe.ProbeAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                result = new PortalModuleHealthProbeResult(false, "health_probe_failed");
            }

            var latencyMs = (int)Math.Clamp(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, int.MaxValue);
            entries.Add(new PortalModuleHealthEntry(module.ModuleId, probe.Kind,
                result.Reachable ? PortalHealthStates.Healthy : PortalHealthStates.Unavailable,
                true, module.ContractVersion, latencyMs, timeProvider.GetUtcNow(), result.ErrorCode,
                result.Capabilities ?? []));
        }

        var enabled = entries.Where(entry => entry.Enabled).ToArray();
        var status = enabled.Any(entry => entry.Status == PortalHealthStates.Unavailable)
            ? PortalHealthStates.Unavailable
            : enabled.Any(entry => entry.Status != PortalHealthStates.Healthy)
                ? PortalHealthStates.Degraded
                : PortalHealthStates.Healthy;
        return new PortalHealthReport(SchemaVersion, options.ResidenceId, timeProvider.GetUtcNow(),
            PortalHealthStates.Healthy, status, entries);
    }
}
