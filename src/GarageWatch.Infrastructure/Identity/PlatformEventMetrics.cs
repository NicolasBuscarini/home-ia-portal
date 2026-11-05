using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace GarageWatch.Infrastructure.Identity;

/// <summary>
/// In-process counters for the EventStore scrape surface. They are deliberately independent of any
/// exporter package: the portal renders the Prometheus text format on demand, so a scrape cannot
/// block ingestion and no external backend is required. Labels are bounded (source, outcome and
/// stable validation reason codes) and never carry payload, actor or resource values.
/// </summary>
public sealed class PlatformEventMetrics
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    private const char LabelSeparator = '\u001f';

    private readonly ConcurrentDictionary<string, long> appended = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> rejected = new(StringComparer.Ordinal);
    private long duplicates;
    private long retentionDeleted;
    private long integrityFailures;
    private long queryCount;
    private long queryMicroseconds;

    public void Appended(string source, string outcome) =>
        Increment(appended, source + LabelSeparator + outcome);

    public void Rejected(string reason) => Increment(rejected, reason);

    public void Duplicated() => Interlocked.Increment(ref duplicates);

    public void RetentionDeleted(int count) => Interlocked.Add(ref retentionDeleted, count);

    public void IntegrityFailed() => Interlocked.Increment(ref integrityFailures);

    public void QueryObserved(TimeSpan duration)
    {
        Interlocked.Increment(ref queryCount);
        Interlocked.Add(ref queryMicroseconds, (long)duration.TotalMicroseconds);
    }

    public string Render()
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "event_store_appended_total", "counter",
            "Eventos da plataforma aceitos pelo writer, por fonte e resultado.");
        foreach (var (key, value) in appended.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var parts = key.Split(LabelSeparator);
            builder.Append("event_store_appended_total{source=\"")
                .Append(Escape(parts[0])).Append("\",outcome=\"")
                .Append(Escape(parts[1])).Append("\"} ")
                .Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        AppendHeader(builder, "event_store_rejected_total", "counter",
            "Eventos recusados na validação, por código estável de erro.");
        foreach (var (reason, value) in rejected.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            builder.Append("event_store_rejected_total{reason=\"")
                .Append(Escape(reason)).Append("\"} ")
                .Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        AppendCounter(builder, "event_store_duplicate_total",
            "Reenvios idempotentes reconhecidos por (source, eventId).",
            Interlocked.Read(ref duplicates));
        AppendCounter(builder, "event_store_retention_deleted_total",
            "Eventos removidos pela retenção por idade.",
            Interlocked.Read(ref retentionDeleted));
        AppendCounter(builder, "event_store_integrity_failures_total",
            "Detalhes consultados cujo hash não confere com o envelope.",
            Interlocked.Read(ref integrityFailures));

        builder.Append("# HELP event_store_query_duration_seconds Duração das consultas administrativas do EventStore.\n")
            .Append("# TYPE event_store_query_duration_seconds summary\n")
            .Append("event_store_query_duration_seconds_sum ")
            .Append((Interlocked.Read(ref queryMicroseconds) / 1_000_000d)
                .ToString("0.######", CultureInfo.InvariantCulture)).Append('\n')
            .Append("event_store_query_duration_seconds_count ")
            .Append(Interlocked.Read(ref queryCount).ToString(CultureInfo.InvariantCulture)).Append('\n');
        return builder.ToString();
    }

    private static void Increment(ConcurrentDictionary<string, long> counters, string key) =>
        counters.AddOrUpdate(key, 1, static (_, current) => current + 1);

    private static void AppendHeader(StringBuilder builder, string name, string type, string help) =>
        builder.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n')
            .Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');

    private static void AppendCounter(StringBuilder builder, string name, string help, long value)
    {
        AppendHeader(builder, name, "counter", help);
        builder.Append(name).Append(' ')
            .Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
