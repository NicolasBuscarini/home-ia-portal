namespace GarageWatch.Application.Portal;

/// <summary>
/// Minimized read-only projection of an external activity service. Titles, URLs and
/// hostnames are deliberately absent; only aggregate application usage is exposed.
/// </summary>
public sealed record ActivityWatchApplicationUsage(string App, double Seconds, int EventCount);

public sealed record ActivityWatchSummary(
    string SchemaVersion,
    string ModuleId,
    bool Reachable,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    double TotalActiveSeconds,
    bool Truncated,
    IReadOnlyList<ActivityWatchApplicationUsage> Applications,
    string? ErrorCode);

public interface IActivityWatchModule
{
    Task<ActivityWatchSummary> GetSummaryAsync(CancellationToken cancellationToken = default);
}
