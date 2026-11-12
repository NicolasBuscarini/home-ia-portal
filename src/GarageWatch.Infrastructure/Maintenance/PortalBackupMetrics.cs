using System.Globalization;
using System.Text;

namespace GarageWatch.Infrastructure.Maintenance;

/// <summary>
/// In-process counters for the portal backup scrape surface. They are deliberately independent of
/// any exporter package: the portal renders the Prometheus text format on demand, so a scrape cannot
/// block a backup and no external backend is required. Labels are absent on purpose — a single
/// resident instance owns the backup — and payloads (file names, hashes) are never emitted.
/// </summary>
public sealed class PortalBackupMetrics
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    private long created;
    private long failed;
    private long deleted;
    private long lastSuccessTimestampSeconds;
    private long lastSuccessSizeBytes;
    private long lastFailureTimestampSeconds;
    private long lastDurationMicroseconds;

    public DateTimeOffset? LastFailureAtUtc => Interlocked.Read(ref lastFailureTimestampSeconds) == 0
        ? null
        : DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref lastFailureTimestampSeconds));

    public void Created(long sizeBytes, DateTimeOffset atUtc)
    {
        Interlocked.Increment(ref created);
        Interlocked.Exchange(ref lastSuccessTimestampSeconds, atUtc.ToUnixTimeSeconds());
        Interlocked.Exchange(ref lastSuccessSizeBytes, sizeBytes);
    }

    public void Failed(DateTimeOffset atUtc)
    {
        Interlocked.Increment(ref failed);
        Interlocked.Exchange(ref lastFailureTimestampSeconds, atUtc.ToUnixTimeSeconds());
    }

    public void Deleted() => Interlocked.Increment(ref deleted);

    public void ObservedDuration(TimeSpan duration) =>
        Interlocked.Exchange(ref lastDurationMicroseconds, (long)duration.TotalMicroseconds);

    public string Render()
    {
        var builder = new StringBuilder();
        AppendCounter(builder, "portal_backup_created_total",
            "Backups do portal criados com sucesso desde o início do processo.", Interlocked.Read(ref created));
        AppendCounter(builder, "portal_backup_failed_total",
            "Backups do portal que falharam desde o início do processo.", Interlocked.Read(ref failed));
        AppendCounter(builder, "portal_backup_deleted_total",
            "Backups do portal removidos por retenção ou manualmente.", Interlocked.Read(ref deleted));
        AppendGauge(builder, "portal_backup_last_success_timestamp_seconds",
            "Unix time do último backup do portal concluído; 0 quando nunca houve sucesso.",
            Interlocked.Read(ref lastSuccessTimestampSeconds));
        AppendGauge(builder, "portal_backup_last_success_size_bytes",
            "Tamanho em bytes (conteúdo em claro) do último backup do portal.",
            Interlocked.Read(ref lastSuccessSizeBytes));
        AppendGauge(builder, "portal_backup_last_failure_timestamp_seconds",
            "Unix time da última falha de backup do portal; 0 quando nunca falhou.",
            Interlocked.Read(ref lastFailureTimestampSeconds));
        AppendGauge(builder, "portal_backup_last_duration_seconds",
            "Duração em segundos do último backup do portal.",
            Interlocked.Read(ref lastDurationMicroseconds) / 1_000_000d);
        return builder.ToString();
    }

    private static void AppendCounter(StringBuilder builder, string name, string help, long value) =>
        AppendMetric(builder, name, "counter", help, value.ToString(CultureInfo.InvariantCulture));

    private static void AppendGauge(StringBuilder builder, string name, string help, long value) =>
        AppendMetric(builder, name, "gauge", help, value.ToString(CultureInfo.InvariantCulture));

    private static void AppendGauge(StringBuilder builder, string name, string help, double value) =>
        AppendMetric(builder, name, "gauge", help, value.ToString("0.####", CultureInfo.InvariantCulture));

    private static void AppendMetric(StringBuilder builder, string name, string type, string help, string value)
    {
        builder.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n')
            .Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n')
            .Append(name).Append(' ').Append(value).Append('\n');
    }
}
