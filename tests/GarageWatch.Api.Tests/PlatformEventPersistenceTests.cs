using GarageWatch.Application.PlatformEvents;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Identity.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class PlatformEventPersistenceTests
{
    private const string PreviousMigration = "20261104173129_AddBiometricPersonLinks";
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Legacy_audit_rows_are_renamed_backfilled_and_preserved()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var occurred = Now.AddDays(-30);
        var id = Guid.NewGuid();

        await fixture.Database.Database.MigrateAsync(PreviousMigration);
        await fixture.Database.Database.ExecuteSqlRawAsync(
            "INSERT INTO IamAuditEvents (Id, ActorUserId, SubjectUserId, Action, ResourceId, OccurredAtUtc) " +
            "VALUES ({0}, NULL, NULL, 'known_person.created', 'PER000000000001', {1})",
            id, occurred.UtcTicks);

        await fixture.Database.Database.MigrateAsync();
        var backfill = new PlatformEventBackfillService(
            fixture.Database, new PublicIdGenerator(), TimeProvider.System);

        var result = await backfill.RunAsync();

        Assert.Equal(1, result.Backfilled);
        var row = await fixture.Database.PlatformEvents.SingleAsync(entry => entry.Id == id);
        Assert.Equal("known_person.created", row.EventType);
        Assert.Equal("PER000000000001", row.ResourceId);
        Assert.Equal(row.Id, row.EventId);
        Assert.True(PlatformEventPublicId.IsValid(row.PublicId));
        Assert.Equal(PlatformEventCategories.Legacy, row.Category);
        Assert.Equal(PlatformEventOutcomes.Unknown, row.Outcome);
        Assert.Equal(PlatformEventSources.IamLegacy, row.Source);
        Assert.Equal(PlatformEventEmitters.Server, row.Emitter);
        Assert.Equal(occurred.UtcTicks, row.RecordedAtUtc?.UtcTicks);
        Assert.Equal(PlatformEventCanonicalizer.ComputePayloadHash(
            PlatformEventBackfillService.ToContract(row)), row.PayloadHash);

        Assert.Equal(0, (await backfill.RunAsync()).Backfilled);
    }

    [Fact]
    public async Task Recorder_promotes_a_catalog_action_to_the_typed_envelope()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.Database.Database.MigrateAsync();
        var recorder = TestRecorder(fixture.Database);

        var result = await recorder.RecordRequiredAsync(new PlatformEventDraft(
            "known_person.created", PlatformEventOutcomes.Success,
            Resource: new PlatformEventReferenceV1("known_person", "PER000000000002")));

        Assert.Equal(PlatformEventWriteStatus.Created, result.Status);
        var row = await fixture.Database.PlatformEvents.SingleAsync();
        Assert.True(PlatformEventPublicId.IsValid(row.PublicId));
        Assert.NotEqual(Guid.Empty, row.EventId);
        Assert.Equal(PlatformEventContract.SchemaVersion, row.SchemaVersion);
        Assert.Equal(PlatformEventCategories.Administration, row.Category);
        Assert.Equal(PlatformEventSeverities.Info, row.Severity);
        Assert.Equal(PlatformEventOutcomes.Success, row.Outcome);
        Assert.Equal(PlatformEventSources.Portal, row.Source);
        Assert.Equal(64, row.PayloadHash!.Length);
    }

    [Fact]
    public async Task Recorder_records_the_explicit_outcome_of_a_denied_action()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.Database.Database.MigrateAsync();
        var recorder = TestRecorder(fixture.Database);

        await recorder.RecordRequiredAsync(new PlatformEventDraft(
            "camera.access.denied", PlatformEventOutcomes.Denied,
            Resource: new PlatformEventReferenceV1("camera", "CAM000000000002")));

        var row = await fixture.Database.PlatformEvents.SingleAsync();
        Assert.Equal(PlatformEventCategories.Authorization, row.Category);
        Assert.Equal(PlatformEventOutcomes.Denied, row.Outcome);
        Assert.Equal(64, row.PayloadHash!.Length);
    }

    [Fact]
    public async Task Recorder_rejects_an_action_outside_the_catalog()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.Database.Database.MigrateAsync();
        var recorder = TestRecorder(fixture.Database);

        var result = await recorder.RecordAsync(new PlatformEventDraft(
            "custom.action", PlatformEventOutcomes.Denied));

        Assert.Equal(PlatformEventWriteStatus.Invalid, result.Status);
        Assert.Equal(0, await fixture.Database.PlatformEvents.CountAsync());
    }

    private static PlatformEventRecorder TestRecorder(IamDbContext database) =>
        new(database,
            new PlatformEventWriter(database, TimeProvider.System,
                NullLogger<PlatformEventWriter>.Instance),
            new NoOriginAccessor(), TimeProvider.System);

    private sealed class NoOriginAccessor : IPlatformEventOriginAccessor
    {
        public PlatformEventOrigin? Current => null;
    }

    [Fact]
    public async Task Source_and_event_id_are_unique()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.Database.Database.MigrateAsync();
        var id = Guid.NewGuid();
        await fixture.Database.Database.ExecuteSqlRawAsync(
            "INSERT INTO PlatformEvents (Id, PublicId, EventId, EventType, Source, OccurredAtUtc) " +
            "VALUES ({0}, 'EVT000000000001', {1}, 'known_person.created', 'portal', {2})", id, id, Now.UtcTicks);

        var duplicate = await Assert.ThrowsAsync<SqliteException>(() =>
            fixture.Database.Database.ExecuteSqlRawAsync(
                "INSERT INTO PlatformEvents (Id, PublicId, EventId, EventType, Source, OccurredAtUtc) " +
                "VALUES ({0}, 'EVT000000000002', {1}, 'known_person.created', 'portal', {2})",
                Guid.NewGuid(), id, Now.UtcTicks));
        Assert.Contains("UNIQUE", duplicate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retention_deletes_old_events_but_keeps_critical_and_today()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.Database.Database.MigrateAsync();
        var options = new PlatformEventRetentionOptions(true, 180, 365, 60, 2);
        var service = new PlatformEventRetentionService(fixture.Database, options, new FixedTimeProvider(Now));

        fixture.Database.PlatformEvents.AddRange(
            Event("EVT000000000011", "old-normal", Now.AddDays(-200), PlatformEventSeverities.Info),
            Event("EVT000000000012", "old-critical", Now.AddDays(-200), PlatformEventSeverities.Critical),
            Event("EVT000000000013", "expired-critical", Now.AddDays(-400), PlatformEventSeverities.Critical),
            Event("EVT000000000014", "recent", Now.AddDays(-179), PlatformEventSeverities.Info),
            Event("EVT000000000015", "today", Now.AddHours(-1), PlatformEventSeverities.Info));
        await fixture.Database.SaveChangesAsync();

        var deleted = await service.RunOnceAsync();

        Assert.Equal(2, deleted);
        var remaining = await fixture.Database.PlatformEvents
            .Select(entry => entry.EventType).OrderBy(value => value).ToArrayAsync();
        Assert.Equal(new[] { "old-critical", "recent", "today" }, remaining);
    }

    [Fact]
    public async Task Default_retention_expires_every_event_after_thirty_days()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.Database.Database.MigrateAsync();
        var options = PlatformEventRetentionOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.Equal(30, options.RetentionDays);
        Assert.Equal(30, options.CriticalRetentionDays);
        var service = new PlatformEventRetentionService(fixture.Database, options, new FixedTimeProvider(Now));

        fixture.Database.PlatformEvents.AddRange(
            Event("EVT000000000021", "old-normal", Now.AddDays(-31), PlatformEventSeverities.Info),
            Event("EVT000000000022", "old-critical", Now.AddDays(-31), PlatformEventSeverities.Critical),
            Event("EVT000000000023", "recent", Now.AddDays(-29), PlatformEventSeverities.Info),
            Event("EVT000000000024", "today", Now.AddHours(-1), PlatformEventSeverities.Info));
        await fixture.Database.SaveChangesAsync();

        var deleted = await service.RunOnceAsync();

        Assert.Equal(2, deleted);
        var remaining = await fixture.Database.PlatformEvents
            .Select(entry => entry.EventType).OrderBy(value => value).ToArrayAsync();
        Assert.Equal(new[] { "recent", "today" }, remaining);
    }

    private static PlatformEvent Event(string publicId, string eventType, DateTimeOffset occurredAt,
        string severity) => new()
        {
            Id = Guid.NewGuid(),
            PublicId = publicId,
            EventId = Guid.NewGuid(),
            SchemaVersion = PlatformEventContract.SchemaVersion,
            EventType = eventType,
            Category = PlatformEventCategories.Legacy,
            Severity = severity,
            Outcome = PlatformEventOutcomes.Unknown,
            Source = PlatformEventSources.Portal,
            Emitter = PlatformEventEmitters.Server,
            OccurredAtUtc = occurredAt,
            RecordedAtUtc = occurredAt,
            PayloadHash = new string('a', 64)
        };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private DatabaseFixture(string path, IamDbContext database)
        {
            Path = path;
            Database = database;
        }

        private string Path { get; }
        public IamDbContext Database { get; }

        public static Task<DatabaseFixture> CreateAsync()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"platform-events-{Guid.NewGuid():D}.db");
            var options = new DbContextOptionsBuilder<IamDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
            return Task.FromResult(new DatabaseFixture(path, new IamDbContext(options, new PublicIdGenerator())));
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}
