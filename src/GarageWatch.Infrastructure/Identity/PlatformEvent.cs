namespace GarageWatch.Infrastructure.Identity;

/// <summary>
/// Append-only platform event row (ADR114). Legacy IAM audit columns are preserved; the platform
/// columns are nullable only until the startup backfill completes, so an interrupted migration is
/// observable and reconcilable on the next start.
/// </summary>
public sealed class PlatformEvent : IHasPublicId
{
    public Guid Id { get; set; }
    public string? PublicId { get; set; }
    public Guid EventId { get; set; }
    public string? SchemaVersion { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Severity { get; set; }
    public string? Outcome { get; set; }
    public string? Source { get; set; }
    public string? SourceVersion { get; set; }
    public string? Emitter { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? SubjectUserId { get; set; }
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public string? CorrelationId { get; set; }
    public string? CausationId { get; set; }
    public string? RequestId { get; set; }
    public string? SessionId { get; set; }
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    public string? HttpMethod { get; set; }
    public string? HttpRoute { get; set; }
    public int? HttpStatusCode { get; set; }
    public long? DurationMs { get; set; }
    public string? ClientUserAgent { get; set; }
    public string? ClientBrowser { get; set; }
    public string? ClientOs { get; set; }
    public string? ClientDevice { get; set; }
    public string? ClientIp { get; set; }
    public string? MetadataJson { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset? RecordedAtUtc { get; set; }
    public string? PayloadHash { get; set; }
    public ApplicationUser? Actor { get; set; }
    public ApplicationUser? Subject { get; set; }
}
