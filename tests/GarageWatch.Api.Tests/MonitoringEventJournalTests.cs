using System.Text.Json;
using GarageWatch.Application.Analysis;
using GarageWatch.Application.Events;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Events;
using GarageWatch.Infrastructure.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class MonitoringEventJournalTests
{
    [Fact]
    public async Task Remote_journal_preserves_the_locally_hashed_payload()
    {
        var root = Path.Combine(Path.GetTempPath(), $"garagewatch-remote-events-{Guid.NewGuid():N}");
        var captured = new CapturingEventsClient();
        try
        {
            var occurrence = Occurrence();
            var local = new LocalEventJournal(new EventEngineOptions(null, null, root, 10, 1_048_576));
            Assert.Equal(EventAppendStatus.Created, await local.AppendAsync(occurrence, CancellationToken.None));
            var storedPath = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Single();
            var stored = JsonDocument.Parse(await File.ReadAllBytesAsync(storedPath));
            var expectedRaw = stored.RootElement.GetProperty("event").GetRawText();
            var expectedHash = stored.RootElement.GetProperty("eventSha256").GetString();

            var remote = new RemoteEventJournal(captured, Microsoft.Extensions.Logging.Abstractions.NullLogger<RemoteEventJournal>.Instance);
            Assert.Equal(EventAppendStatus.Created, await remote.AppendAsync(occurrence, CancellationToken.None));
            Assert.NotNull(captured.Envelope);
            Assert.Equal(expectedHash, captured.Envelope!.EventSha256);
            Assert.Equal("1.0", captured.Envelope.SchemaVersion);
            Assert.Equal(expectedRaw, captured.Envelope.Event.GetRawText());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Remote_journal_maps_capacity_duplicate_and_recovers_counts()
    {
        var client = new CapturingEventsClient
        {
            Outcome = new MonitoringEventAppendOutcome("capacity_reached", true),
            Recovery = new MonitoringEventRecovery(3, 1, 512)
        };
        var journal = new RemoteEventJournal(client,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RemoteEventJournal>.Instance);
        Assert.Equal(EventAppendStatus.CapacityReached, await journal.AppendAsync(Occurrence(),
            CancellationToken.None));

        client.Outcome = new MonitoringEventAppendOutcome("duplicate", false);
        Assert.Equal(EventAppendStatus.Duplicate, await journal.AppendAsync(Occurrence(), CancellationToken.None));

        var recovery = await journal.RecoverAsync(CancellationToken.None);
        Assert.Equal(3, recovery.RecoveredEvents);
        Assert.Equal(1, recovery.InvalidEvents);
        Assert.Equal(512, recovery.JournalBytes);

        client.Outcome = null;
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            journal.AppendAsync(Occurrence(), CancellationToken.None));
        client.Outcome = new MonitoringEventAppendOutcome("created", false);
        client.Recovery = null;
        var unavailable = await journal.RecoverAsync(CancellationToken.None);
        Assert.Equal(0, unavailable.RecoveredEvents);
    }

    [Fact]
    public async Task Remote_source_selects_the_remote_journal_and_local_is_the_default()
    {
        var client = new CapturingEventsClient();
        await using (var remoteFactory = new ApiFactory(monitoringEventsSource: "remote",
                         monitoringEventsClient: client))
        {
            using var scope = remoteFactory.Services.CreateScope();
            Assert.IsType<RemoteEventJournal>(scope.ServiceProvider.GetRequiredService<IEventJournal>());
        }
        await using (var localFactory = new ApiFactory())
        {
            using var scope = localFactory.Services.CreateScope();
            Assert.IsType<LocalEventJournal>(scope.ServiceProvider.GetRequiredService<IEventJournal>());
        }
    }

    [Fact]
    public void Invalid_events_source_is_rejected()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MONITORING_EVENTS_SOURCE"] = "invalid",
                ["MONITORING_BASE_URL"] = "http://monitoring.invalid"
            }).Build();
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = MonitoringModuleOptions.FromConfiguration(configuration);
        });
    }

    private static EventOccurrenceV1 Occurrence() => new("1.0", Guid.NewGuid().ToString("N"), "garage",
        "garage-01", "epoch-1", ["track-1"], "person.in_zone", EventAlertLevel.Attention, "config-1",
        new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 11, 2, 12, 0, 31, TimeSpan.Zero), "dwell", 1,
        new EventEvidenceV1("zone.dwell.threshold_reached", 31, new NormalizedPoint(.5, .5),
            new DetectionBox(.4f, .4f, .6f, .6f), .9f, null),
        new DateTimeOffset(2026, 11, 2, 12, 0, 31, TimeSpan.Zero));

    private sealed class CapturingEventsClient : IMonitoringEventsClient
    {
        public MonitoringEventEnvelope? Envelope { get; private set; }
        public MonitoringEventAppendOutcome? Outcome { get; set; } = new("created", false);
        public MonitoringEventRecovery? Recovery { get; set; } = new(0, 0, 0);

        public Task<MonitoringEventAppendOutcome?> AppendAsync(MonitoringEventEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            Envelope = envelope;
            return Task.FromResult(Outcome);
        }

        public Task<MonitoringEventRecovery?> GetRecoveryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Recovery);
    }
}
