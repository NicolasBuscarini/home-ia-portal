using System.Text.Json;

namespace GarageWatch.Application.PlatformEvents;

/// <summary>Validated administrative query over the platform event store.</summary>
public sealed record PlatformEventQuery(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? Actor,
    string? EventType,
    string? Category,
    string? Outcome,
    string? Source,
    string? CorrelationId,
    string? ResourceType,
    string? ResourceId,
    string? Search,
    string? Browser,
    string? Os,
    string? Device,
    int PageSize,
    PlatformEventCursor? Cursor)
{
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 100;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(180);
}

public sealed record PlatformEventListItem(
    string PublicId,
    DateTimeOffset OccurredAtUtc,
    string EventType,
    string Category,
    string Severity,
    string Outcome,
    string Source,
    string? ActorPublicId,
    string? ActorUsername,
    string? ResourceType,
    string? ResourceId,
    string? CorrelationId);

public sealed record PlatformEventPage(IReadOnlyList<PlatformEventListItem> Items, string? NextCursor);

public sealed record PlatformEventDetail(
    PlatformEventRecordV1 Record,
    string? ActorPublicId,
    string? SubjectPublicId,
    bool IntegrityValid,
    IReadOnlyDictionary<string, JsonElement>? Metadata);

public interface IPlatformEventQueryService
{
    Task<PlatformEventPage> ListAsync(PlatformEventQuery query, CancellationToken cancellationToken = default);
    Task<PlatformEventDetail?> GetAsync(string publicId, CancellationToken cancellationToken = default);
}

/// <summary>Opaque keyset cursor: the last (occurredAtUtc, id) pair, never an offset.</summary>
public readonly record struct PlatformEventCursor(DateTimeOffset OccurredAtUtc, Guid Id)
{
    public static string Encode(DateTimeOffset occurredAtUtc, Guid id) =>
        System.Buffers.Text.Base64Url.EncodeToString(
            System.Text.Encoding.UTF8.GetBytes(
                $"{occurredAtUtc.UtcTicks:D}:{id:D}"));

    public static bool TryDecode(string? value, out PlatformEventCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            var raw = System.Text.Encoding.UTF8.GetString(
                System.Buffers.Text.Base64Url.DecodeFromChars(value));
            var parts = raw.Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks)
                || !Guid.TryParse(parts[1], out var id))
                return false;
            cursor = new PlatformEventCursor(
                new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
