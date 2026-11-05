namespace GarageWatch.Application.PlatformEvents;

/// <summary>
/// Action-oriented input for portal-originated events. Category, severity, source and emitter are
/// resolved server-side; callers describe only the fact and its public references.
/// </summary>
public sealed record PlatformEventDraft(
    string EventType,
    string Outcome,
    PlatformEventReferenceV1? Subject = null,
    PlatformEventReferenceV1? Resource = null,
    IReadOnlyDictionary<string, object?>? Metadata = null,
    string? CausationId = null,
    string? SourceVersion = null,
    DateTimeOffset? OccurredAtUtc = null);

/// <summary>
/// Request context captured by the HTTP host. It is intentionally actor-id based: the recorder
/// resolves the immutable public id, so callers cannot forge who performed the action.
/// </summary>
public sealed record PlatformEventOrigin(
    Guid? ActorUserId,
    string? SessionId,
    string CorrelationId,
    string RequestId,
    string? TraceId,
    string? SpanId,
    PlatformEventClientV1? Client);

public interface IPlatformEventOriginAccessor
{
    PlatformEventOrigin? Current { get; }
}

public interface IPlatformEventRecorder
{
    Task<PlatformEventWriteResult> RecordAsync(PlatformEventDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>Mandatory actions must fail closed when the event cannot be appended.</summary>
    Task<PlatformEventWriteResult> RecordRequiredAsync(PlatformEventDraft draft,
        CancellationToken cancellationToken = default);
}

public sealed class PlatformEventRecordingException(string error)
    : Exception($"Falha ao registrar evento da plataforma ({error}).")
{
    public string Error { get; } = error;
}
