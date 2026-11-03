namespace GarageWatch.Application.Portal;

/// <summary>
/// Sanitized health state shared by the portal aggregate and its module projections.
/// </summary>
public static class PortalHealthStates
{
    public const string Healthy = "healthy";
    public const string Degraded = "degraded";
    public const string Unavailable = "unavailable";
    public const string Disabled = "disabled";
    public const string Unknown = "unknown";
}

public static class PortalModuleKinds
{
    public const string Module = "module";
    public const string Integration = "integration";
}

public sealed record PortalModuleHealthProbeResult(
    bool Reachable,
    string? ErrorCode,
    IReadOnlyList<string>? Capabilities = null);

/// <summary>
/// Project-owned health probe for one catalog module. A probe is only added for a module whose
/// runtime boundary exists, so an unknown module is reported instead of guessed by URL.
/// </summary>
public interface IPortalModuleHealthProbe
{
    string ModuleId { get; }
    string Kind { get; }
    Task<PortalModuleHealthProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}

public sealed record PortalModuleHealthEntry(
    string ModuleId,
    string Kind,
    string Status,
    bool Enabled,
    string? ContractVersion,
    int LatencyMs,
    DateTimeOffset CheckedAtUtc,
    string? ErrorCode,
    IReadOnlyList<string> Capabilities);

public sealed record PortalHealthReport(
    string SchemaVersion,
    string ResidenceId,
    DateTimeOffset GeneratedAtUtc,
    string PortalStatus,
    string Status,
    IReadOnlyList<PortalModuleHealthEntry> Modules);

public interface IPortalHealth
{
    Task<PortalHealthReport> GetAsync(CancellationToken cancellationToken = default);
}
