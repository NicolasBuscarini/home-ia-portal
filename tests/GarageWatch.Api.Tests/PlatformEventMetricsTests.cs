using GarageWatch.Infrastructure.Identity;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class PlatformEventMetricsTests
{
    [Fact]
    public void Render_exposes_bounded_counters_in_prometheus_text_format()
    {
        var metrics = new PlatformEventMetrics();
        metrics.Appended("portal", "success");
        metrics.Appended("portal", "success");
        metrics.Appended("monitoring", "denied");
        metrics.Rejected("unknown_event_type");
        metrics.Duplicated();
        metrics.RetentionDeleted(3);
        metrics.IntegrityFailed();
        metrics.QueryObserved(TimeSpan.FromMilliseconds(250));

        var text = metrics.Render();

        Assert.Contains("# TYPE event_store_appended_total counter", text);
        Assert.Contains("event_store_appended_total{source=\"monitoring\",outcome=\"denied\"} 1", text);
        Assert.Contains("event_store_appended_total{source=\"portal\",outcome=\"success\"} 2", text);
        Assert.Contains("event_store_rejected_total{reason=\"unknown_event_type\"} 1", text);
        Assert.Contains("event_store_duplicate_total 1", text);
        Assert.Contains("event_store_retention_deleted_total 3", text);
        Assert.Contains("event_store_integrity_failures_total 1", text);
        Assert.Contains("event_store_query_duration_seconds_sum 0.25", text);
        Assert.Contains("event_store_query_duration_seconds_count 1", text);
    }

    [Fact]
    public void Render_is_empty_but_valid_before_any_event()
    {
        var text = new PlatformEventMetrics().Render();

        Assert.Contains("# TYPE event_store_appended_total counter", text);
        Assert.DoesNotContain("event_store_appended_total{", text);
        Assert.Contains("event_store_query_duration_seconds_count 0", text);
    }

    [Fact]
    public void Render_escapes_label_values()
    {
        var metrics = new PlatformEventMetrics();
        metrics.Rejected("bad\"reason\\with\nbreak");

        Assert.Contains("reason=\"bad\\\"reason\\\\with\\nbreak\"", metrics.Render());
    }
}
