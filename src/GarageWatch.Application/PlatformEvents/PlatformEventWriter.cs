namespace GarageWatch.Application.PlatformEvents;

public enum PlatformEventWriteStatus
{
    Created,
    Duplicate,
    Invalid
}

public sealed record PlatformEventWriteResult(
    PlatformEventWriteStatus Status, string? PublicId = null, string? Error = null)
{
    public static PlatformEventWriteResult Created(string publicId) =>
        new(PlatformEventWriteStatus.Created, publicId);

    public static PlatformEventWriteResult Duplicate(string publicId) =>
        new(PlatformEventWriteStatus.Duplicate, publicId);

    public static PlatformEventWriteResult Invalid(string error) =>
        new(PlatformEventWriteStatus.Invalid, Error: error);
}

/// <summary>
/// Server-side append boundary of the event store. Ingestion validates, resolves actor/subject
/// identities and enforces idempotency by <c>(source, eventId)</c>; callers never write EF rows.
/// </summary>
public interface IPlatformEventWriter
{
    Task<PlatformEventWriteResult> WriteAsync(PlatformEventV1 value,
        CancellationToken cancellationToken = default);
}

public static class PlatformEventIngestionPolicy
{
    public static bool IsBrowserEventType(string eventType) =>
        eventType.StartsWith("ui.", StringComparison.Ordinal) ||
        eventType.StartsWith("navigation.", StringComparison.Ordinal);

    public static bool IsServiceSource(string source) => source is
        PlatformEventSources.Monitoring or PlatformEventSources.System;
}
