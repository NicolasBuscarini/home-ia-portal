using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Identity;

/// <summary>
/// Retention of the identity triage store. Observations and their append-only reviews are removed
/// together after the same TTL; there is no separate dataset or model copy. The TTL is measured from
/// server ingestion, not from the producer-supplied observation time.
/// </summary>
public sealed record IdentityObservationRetentionOptions(
    bool Enabled, int RetentionDays, int SweepIntervalMinutes, int BatchSize)
{
    public const int DefaultRetentionDays = 30;
    public const int DefaultSweepIntervalMinutes = 60;
    public const int DefaultBatchSize = 200;

    public static IdentityObservationRetentionOptions FromConfiguration(IConfiguration configuration)
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
        var rawEnabled = configuration["IDENTITY_OBSERVATION_RETENTION_ENABLED"];
        if (!string.IsNullOrWhiteSpace(rawEnabled) && !bool.TryParse(rawEnabled, out enabled))
            throw new InvalidOperationException("IDENTITY_OBSERVATION_RETENTION_ENABLED deve ser booleano.");

        return new IdentityObservationRetentionOptions(
            enabled,
            Number(configuration, "IDENTITY_OBSERVATION_RETENTION_DAYS", DefaultRetentionDays, 1, 3650),
            Number(configuration, "IDENTITY_OBSERVATION_RETENTION_SWEEP_MINUTES",
                DefaultSweepIntervalMinutes, 1, 1440),
            Number(configuration, "IDENTITY_OBSERVATION_RETENTION_BATCH_SIZE", DefaultBatchSize, 1, 2000));
    }
}
