using GarageWatch.Application.PlatformEvents;
using GarageWatch.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class PlatformEventRecorderTests
{
    [Fact]
    public async Task Recorder_builds_a_correlated_portal_event_from_the_authenticated_origin()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.Database.Database.MigrateAsync();
        var actorId = Guid.NewGuid();
        fixture.Database.Users.Add(new ApplicationUser
        {
            Id = actorId,
            UserName = "recorder-actor",
            NormalizedUserName = "RECORDER-ACTOR",
            PublicId = "USR000000000099",
            IsActive = true,
            SecurityStamp = "recorder-security-stamp"
        });
        await fixture.Database.SaveChangesAsync();

        var origin = new PlatformEventOrigin(actorId, "session-1", "a1b2c3d4-1111-4222-8333-444455556666",
            "request-1", "0123456789abcdef0123456789abcdef", "0123456789abcdef",
            new PlatformEventClientV1("Recorder/1.0", "Firefox", "Linux", "desktop", "127.0.0.1"));
        var recorder = new PlatformEventRecorder(fixture.Database,
            new PlatformEventWriter(fixture.Database, TimeProvider.System,
                NullLogger<PlatformEventWriter>.Instance),
            new StaticOriginAccessor(origin), TimeProvider.System);

        var result = await recorder.RecordRequiredAsync(new PlatformEventDraft(
            "camera.connection.address.updated", PlatformEventOutcomes.Success,
            Subject: new PlatformEventReferenceV1("user", "USR000000000099"),
            Resource: new PlatformEventReferenceV1("camera", "CAM000000000001")));

        Assert.Equal(PlatformEventWriteStatus.Created, result.Status);
        var row = await fixture.Database.PlatformEvents.SingleAsync();
        Assert.Equal(actorId, row.ActorUserId);
        Assert.Equal(actorId, row.SubjectUserId);
        Assert.Equal("administration", row.Category);
        Assert.Equal("info", row.Severity);
        Assert.Equal("success", row.Outcome);
        Assert.Equal("portal", row.Source);
        Assert.Equal("server", row.Emitter);
        Assert.Equal("camera", row.ResourceType);
        Assert.Equal("CAM000000000001", row.ResourceId);
        Assert.Equal(origin.CorrelationId, row.CorrelationId);
        Assert.Equal(origin.RequestId, row.RequestId);
        Assert.Equal(origin.SessionId, row.SessionId);
        Assert.Equal(origin.TraceId, row.TraceId);
        Assert.Equal(origin.SpanId, row.SpanId);
        Assert.Equal("Recorder/1.0", row.ClientUserAgent);
        Assert.Equal("Firefox", row.ClientBrowser);
        Assert.Equal("Linux", row.ClientOs);
        Assert.Equal("desktop", row.ClientDevice);
        Assert.Equal("127.0.0.1", row.ClientIp);
        Assert.Equal(15, row.PublicId!.Length);
        Assert.Equal(64, row.PayloadHash!.Length);
    }

    [Fact]
    public async Task Required_recording_fails_closed_for_unknown_type_or_unresolved_actor()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.Database.Database.MigrateAsync();
        var recorder = new PlatformEventRecorder(fixture.Database,
            new PlatformEventWriter(fixture.Database, TimeProvider.System,
                NullLogger<PlatformEventWriter>.Instance),
            new StaticOriginAccessor(new PlatformEventOrigin(Guid.NewGuid(), null,
                "a1b2c3d4-1111-4222-8333-444455556666", "request-1", null, null, null)),
            TimeProvider.System);

        var unknown = await recorder.RecordAsync(new PlatformEventDraft(
            "unknown.event", PlatformEventOutcomes.Success));
        Assert.Equal(PlatformEventWriteStatus.Invalid, unknown.Status);
        Assert.Equal(PlatformEventValidationErrors.UnknownEventType, unknown.Error);

        var missingActor = await recorder.RecordAsync(new PlatformEventDraft(
            "camera.connection.address.updated", PlatformEventOutcomes.Success));
        Assert.Equal(PlatformEventWriteStatus.Invalid, missingActor.Status);
        Assert.Equal(PlatformEventValidationErrors.InvalidActor, missingActor.Error);

        await Assert.ThrowsAsync<PlatformEventRecordingException>(() => recorder.RecordRequiredAsync(
            new PlatformEventDraft("unknown.event", PlatformEventOutcomes.Success)));
        Assert.Equal(0, await fixture.Database.PlatformEvents.CountAsync());
    }

    private sealed class StaticOriginAccessor(PlatformEventOrigin origin) : IPlatformEventOriginAccessor
    {
        public PlatformEventOrigin? Current { get; } = origin;
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
                $"platform-event-recorder-{Guid.NewGuid():D}.db");
            var options = new DbContextOptionsBuilder<IamDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
            return Task.FromResult(new DatabaseFixture(path,
                new IamDbContext(options, new PublicIdGenerator())));
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}
