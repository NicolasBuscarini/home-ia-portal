namespace GarageWatch.Application.PlatformEvents;

/// <summary>
/// Versioned envelope of the platform append-only EventStore (ADR114). Emitters supply the event
/// fields; <c>publicId</c>, <c>recordedAtUtc</c> and <c>payloadHash</c> are server-owned and only
/// exist on <see cref="PlatformEventRecordV1"/>.
/// </summary>
public static class PlatformEventContract
{
    public const string SchemaVersion = "1.0";
    public const int MinimumCorrelationIdLength = 8;
    public const int MaximumCorrelationIdLength = 128;
    public const int MaximumMetadataKeys = 20;
    public const int MaximumMetadataBytes = 4096;
    public const int MaximumUserAgentLength = 512;
    public const int MaximumClientLabelLength = 64;
    public const int MaximumReferenceTypeLength = 40;
    public const int MaximumReferenceIdLength = 64;
    public const int MaximumOpaqueIdLength = 128;
    public const int MaximumRouteLength = 256;
    public const int MaximumDurationMs = 600_000;
    public const int MaximumFutureSkewMinutes = 5;

    /// <summary>Canonical JSON fields of the event portion, in envelope order.</summary>
    public static IReadOnlyList<string> EventFieldNames { get; } =
    [
        "schemaVersion", "eventId", "eventType", "category", "severity", "outcome",
        "occurredAtUtc", "source", "sourceVersion", "emitter", "actor", "subject", "resource",
        "correlationId", "causationId", "requestId", "sessionId", "traceId", "spanId",
        "http", "client", "metadata"
    ];

    /// <summary>Persisted envelope fields; the last three are computed by the server.</summary>
    public static IReadOnlyList<string> StoredFieldNames { get; } =
        [.. EventFieldNames, "publicId", "recordedAtUtc", "payloadHash"];
}

public static class PlatformEventCategories
{
    public const string Authentication = "authentication";
    public const string Authorization = "authorization";
    public const string Administration = "administration";
    public const string Configuration = "configuration";
    public const string Media = "media";
    public const string Biometrics = "biometrics";
    public const string Integration = "integration";
    public const string Navigation = "navigation";
    public const string System = "system";
    public const string Legacy = "legacy";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Authentication, Authorization, Administration, Configuration, Media, Biometrics,
        Integration, Navigation, System, Legacy
    };
}

public static class PlatformEventSeverities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";
    public const string Critical = "critical";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Info, Warning, Error, Critical
    };
}

public static class PlatformEventOutcomes
{
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Denied = "denied";
    public const string Error = "error";
    public const string Unknown = "unknown";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Success, Failure, Denied, Error, Unknown
    };
}

public static class PlatformEventEmitters
{
    public const string Server = "server";
    public const string Service = "service";
    public const string Browser = "browser";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Server, Service, Browser
    };
}

/// <summary>
/// Sources are limited to the platform-owned names and registered module identifiers. Ingestion
/// still authorizes the source against the session or service token (T17-C).
/// </summary>
public static class PlatformEventSources
{
    public const string Portal = "portal";
    public const string Monitoring = "monitoring";
    public const string ActivityWatch = "activitywatch";
    public const string Shell = "shell";
    public const string System = "system";
    public const string Legacy = "legacy";
    public const string IamLegacy = "iam-legacy";
    public const int MaximumLength = 32;

    public static bool IsValid(string? value) =>
        value is { Length: > 0 and <= MaximumLength } &&
        char.IsAsciiLetterLower(value[0]) &&
        value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');
}

/// <summary>Server-assigned identifier for a stored event. Never derived from the internal key.</summary>
public static class PlatformEventPublicId
{
    public const string Prefix = "EVT";
    public const int Digits = 12;

    public static bool IsValid(string? value) =>
        value is not null && value.Length == Prefix.Length + Digits &&
        value.StartsWith(Prefix, StringComparison.Ordinal) &&
        value.AsSpan(Prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
}

public sealed record PlatformEventActorV1(string? UserPublicId, string? Username, string? DisplayName);

public sealed record PlatformEventReferenceV1(string Type, string PublicId);

public sealed record PlatformEventHttpV1(string Method, string Route, int StatusCode, long DurationMs);

public sealed record PlatformEventClientV1(
    string? UserAgent, string? Browser, string? Os, string? Device, string? Ip);

/// <summary>Emitter-owned event payload validated by <see cref="PlatformEventValidator"/>.</summary>
public sealed record PlatformEventV1(
    Guid EventId,
    string EventType,
    string Category,
    string Severity,
    string Outcome,
    DateTimeOffset OccurredAtUtc,
    string Source,
    string? SourceVersion,
    string Emitter,
    PlatformEventActorV1? Actor = null,
    PlatformEventReferenceV1? Subject = null,
    PlatformEventReferenceV1? Resource = null,
    string? CorrelationId = null,
    string? CausationId = null,
    string? RequestId = null,
    string? SessionId = null,
    string? TraceId = null,
    string? SpanId = null,
    PlatformEventHttpV1? Http = null,
    PlatformEventClientV1? Client = null,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    public string SchemaVersion { get; init; } = PlatformEventContract.SchemaVersion;
}

/// <summary>Persisted event: emitter payload plus the server-owned identifier, time and hash.</summary>
public sealed record PlatformEventRecordV1(
    string PublicId,
    DateTimeOffset RecordedAtUtc,
    string PayloadHash,
    PlatformEventV1 Event);
