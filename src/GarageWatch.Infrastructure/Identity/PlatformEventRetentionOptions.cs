using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Identity;

/// <summary>
/// Retention of the platform event store. Critical events are kept for the longer window; the
/// current UTC day is never pruned, so an operator can always inspect today after an incident.
/// </summary>
public sealed record PlatformEventRetentionOptions(
    bool Enabled, int RetentionDays, int CriticalRetentionDays, int SweepIntervalMinutes, int BatchSize)
{
    public const int DefaultRetentionDays = 180;
    public const int DefaultCriticalRetentionDays = 365;
    public const int DefaultSweepIntervalMinutes = 60;
    public const int DefaultBatchSize = 500;

    public static PlatformEventRetentionOptions FromConfiguration(IConfiguration configuration)
    {
        static int Number(IConfiguration source, string key, int fallback, int minimum, int maximum)
        {
            var raw = source[key];
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var value)
                || value < minimum || value > maximum)
                throw new InvalidOperationException($"{key} fora do limite permitido.");
            return value;
        }

        var enabled = true;
        var rawEnabled = configuration["EVENT_STORE_RETENTION_ENABLED"];
        if (!string.IsNullOrWhiteSpace(rawEnabled))
        {
            if (!bool.TryParse(rawEnabled, out enabled))
                throw new InvalidOperationException("EVENT_STORE_RETENTION_ENABLED deve ser booleano.");
        }

        var retentionDays = Number(configuration, "EVENT_STORE_RETENTION_DAYS", DefaultRetentionDays, 1, 3650);
        var criticalRetentionDays = Number(configuration, "EVENT_STORE_CRITICAL_RETENTION_DAYS",
            DefaultCriticalRetentionDays, 1, 3650);
        if (criticalRetentionDays < retentionDays)
            throw new InvalidOperationException(
                "EVENT_STORE_CRITICAL_RETENTION_DAYS deve ser maior ou igual a EVENT_STORE_RETENTION_DAYS.");

        return new PlatformEventRetentionOptions(enabled, retentionDays, criticalRetentionDays,
            Number(configuration, "EVENT_STORE_RETENTION_SWEEP_MINUTES", DefaultSweepIntervalMinutes, 1, 1440),
            Number(configuration, "EVENT_STORE_RETENTION_BATCH_SIZE", DefaultBatchSize, 1, 5000));
    }
}
