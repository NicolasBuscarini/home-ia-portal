using System.Diagnostics;
using System.Text.Json;
using GarageWatch.Application.PlatformEvents;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public sealed class PlatformEventQueryService(IamDbContext database, PlatformEventMetrics? metrics = null)
    : IPlatformEventQueryService
{
    public async Task<PlatformEventPage> ListAsync(PlatformEventQuery query,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var events = Filtered(query);

        if (query.Cursor is { } cursor)
        {
            var cursorId = cursor.Id;
            events = events.Where(entry => entry.OccurredAtUtc < cursor.OccurredAtUtc
                || (entry.OccurredAtUtc == cursor.OccurredAtUtc && entry.Id.CompareTo(cursorId) < 0));
        }

        var rows = await events
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .ThenByDescending(entry => entry.Id)
            .Take(query.PageSize + 1)
            .Select(entry => new
            {
                entry.Id,
                entry.PublicId,
                entry.OccurredAtUtc,
                entry.EventType,
                Category = entry.Category ?? PlatformEventCategories.Legacy,
                Severity = entry.Severity ?? PlatformEventSeverities.Info,
                Outcome = entry.Outcome ?? PlatformEventOutcomes.Unknown,
                Source = entry.Source ?? PlatformEventSources.IamLegacy,
                ActorPublicId = entry.Actor != null ? entry.Actor.PublicId : null,
                ActorUsername = entry.Actor != null ? entry.Actor.UserName : null,
                entry.ResourceType,
                entry.ResourceId,
                entry.CorrelationId
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > query.PageSize;
        var page = hasMore ? rows.Take(query.PageSize).ToList() : rows;
        var items = page.Select(row => new PlatformEventListItem(
            row.PublicId!, row.OccurredAtUtc, row.EventType, row.Category, row.Severity,
            row.Outcome, row.Source, row.ActorPublicId, row.ActorUsername,
            row.ResourceType, row.ResourceId, row.CorrelationId)).ToArray();
        var nextCursor = hasMore && page.Count > 0
            ? PlatformEventCursor.Encode(page[^1].OccurredAtUtc, page[^1].Id)
            : null;
        metrics?.QueryObserved(Stopwatch.GetElapsedTime(started));
        return new PlatformEventPage(items, nextCursor);
    }

    public async Task<PlatformEventDetail?> GetAsync(string publicId,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var entity = await database.PlatformEvents.AsNoTracking()
            .Include(entry => entry.Actor)
            .Include(entry => entry.Subject)
            .SingleOrDefaultAsync(entry => entry.PublicId == publicId, cancellationToken);
        if (entity is null)
        {
            metrics?.QueryObserved(Stopwatch.GetElapsedTime(started));
            return null;
        }

        var actorPublicId = entity.Actor?.PublicId;
        var subjectPublicId = entity.Subject?.PublicId;
        var (value, metadata) = ToContract(entity, actorPublicId, subjectPublicId);
        var integrityValid = entity.PayloadHash is { Length: 64 }
            && string.Equals(entity.PayloadHash,
                PlatformEventCanonicalizer.ComputePayloadHash(value), StringComparison.OrdinalIgnoreCase);
        if (!integrityValid) metrics?.IntegrityFailed();
        metrics?.QueryObserved(Stopwatch.GetElapsedTime(started));
        var recorded = new PlatformEventRecordV1(
            entity.PublicId!, entity.RecordedAtUtc ?? entity.OccurredAtUtc,
            entity.PayloadHash ?? string.Empty, value);
        return new PlatformEventDetail(recorded, actorPublicId, subjectPublicId,
            integrityValid, ReadMetadata(entity.MetadataJson));
    }

    private IQueryable<PlatformEvent> Filtered(PlatformEventQuery query)
    {
        var events = database.PlatformEvents.AsNoTracking().Include(entry => entry.Actor).AsQueryable();
        events = events.Where(entry => entry.OccurredAtUtc >= query.FromUtc
            && entry.OccurredAtUtc <= query.ToUtc);

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            var actor = query.Actor.Trim();
            events = PlatformEventPublicId.IsValid(actor)
                ? events.Where(entry => entry.Actor != null && entry.Actor.PublicId == actor)
                : events.Where(entry => entry.Actor != null && entry.Actor.UserName!.Contains(actor));
        }

        if (query.EventType is { Length: > 0 } eventType)
            events = events.Where(entry => entry.EventType == eventType);
        if (query.Category is { Length: > 0 } category)
            events = events.Where(entry => entry.Category == category);
        if (query.Outcome is { Length: > 0 } outcome)
            events = events.Where(entry => entry.Outcome == outcome);
        if (query.Source is { Length: > 0 } source)
            events = events.Where(entry => entry.Source == source);
        if (query.CorrelationId is { Length: > 0 } correlation)
            events = events.Where(entry => entry.CorrelationId == correlation);
        if (query.ResourceType is { Length: > 0 } resourceType)
            events = events.Where(entry => entry.ResourceType == resourceType);
        if (query.ResourceId is { Length: > 0 } resourceId)
            events = events.Where(entry => entry.ResourceId == resourceId);
        if (query.Browser is { Length: > 0 } browser)
            events = events.Where(entry => entry.ClientBrowser == browser);
        if (query.Os is { Length: > 0 } os)
            events = events.Where(entry => entry.ClientOs == os);
        if (query.Device is { Length: > 0 } device)
            events = events.Where(entry => entry.ClientDevice == device);
        if (query.Search is { Length: > 0 } search)
            events = events.Where(entry => entry.EventType.Contains(search)
                || (entry.ResourceId != null && entry.ResourceId.Contains(search))
                || (entry.CorrelationId != null && entry.CorrelationId.Contains(search)));
        return events;
    }

    private static (PlatformEventV1 Value, IReadOnlyDictionary<string, object?>? Metadata) ToContract(
        PlatformEvent entity, string? actorPublicId, string? subjectPublicId)
    {
        if (string.Equals(entity.Category, PlatformEventCategories.Legacy, StringComparison.Ordinal))
            return (PlatformEventBackfillService.ToContract(entity, actorPublicId), null);

        IReadOnlyDictionary<string, object?>? metadata = null;
        try
        {
            metadata = PlatformEventCanonicalizer.DeserializeMetadata(entity.MetadataJson);
        }
        catch (JsonException)
        {
            // A malformed stored metadata makes the integrity check fail; the raw value is not exposed.
        }

        var http = entity.HttpMethod is not null && entity.HttpRoute is not null
            && entity.HttpStatusCode is not null && entity.DurationMs is not null
            ? new PlatformEventHttpV1(entity.HttpMethod, entity.HttpRoute,
                entity.HttpStatusCode.Value, entity.DurationMs.Value)
            : null;
        var client = entity.ClientUserAgent is not null || entity.ClientBrowser is not null
            || entity.ClientOs is not null || entity.ClientDevice is not null || entity.ClientIp is not null
            ? new PlatformEventClientV1(entity.ClientUserAgent, entity.ClientBrowser,
                entity.ClientOs, entity.ClientDevice, entity.ClientIp)
            : null;
        var value = new PlatformEventV1(
            entity.EventId, entity.EventType,
            entity.Category ?? PlatformEventCategories.Legacy,
            entity.Severity ?? PlatformEventSeverities.Info,
            entity.Outcome ?? PlatformEventOutcomes.Unknown,
            new DateTimeOffset(entity.OccurredAtUtc.UtcTicks, TimeSpan.Zero),
            entity.Source ?? PlatformEventSources.IamLegacy,
            entity.SourceVersion,
            entity.Emitter ?? PlatformEventEmitters.Server,
            actorPublicId is null ? null : new PlatformEventActorV1(actorPublicId, null, null),
            subjectPublicId is null ? null : new PlatformEventReferenceV1("user", subjectPublicId),
            entity.ResourceType is not null && entity.ResourceId is not null
                ? new PlatformEventReferenceV1(entity.ResourceType, entity.ResourceId)
                : null,
            entity.CorrelationId, entity.CausationId, entity.RequestId, entity.SessionId,
            entity.TraceId, entity.SpanId, http, client, metadata)
        {
            SchemaVersion = entity.SchemaVersion ?? PlatformEventContract.SchemaVersion
        };
        return (value, metadata);
    }

    private static IReadOnlyDictionary<string, JsonElement>? ReadMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            return document.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(),
                    StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
