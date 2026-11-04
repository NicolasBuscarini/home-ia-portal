using System.Net;

namespace GarageWatch.Application.PlatformEvents;

public static class PlatformEventValidationErrors
{
    public const string InvalidEvent = "invalid_event";
    public const string UnsupportedSchemaVersion = "unsupported_schema_version";
    public const string InvalidEventType = "invalid_event_type";
    public const string UnknownEventType = "unknown_event_type";
    public const string EventTypeCategoryMismatch = "event_type_category_mismatch";
    public const string InvalidCategory = "invalid_category";
    public const string InvalidSeverity = "invalid_severity";
    public const string InvalidOutcome = "invalid_outcome";
    public const string InvalidOccurredAt = "invalid_occurred_at";
    public const string InvalidSource = "invalid_source";
    public const string MissingSourceVersion = "missing_source_version";
    public const string InvalidSourceVersion = "invalid_source_version";
    public const string InvalidEmitter = "invalid_emitter";
    public const string InvalidActor = "invalid_actor";
    public const string InvalidReference = "invalid_reference";
    public const string InvalidCorrelationId = "invalid_correlation_id";
    public const string InvalidCausationId = "invalid_causation_id";
    public const string InvalidRequestId = "invalid_request_id";
    public const string InvalidSessionId = "invalid_session_id";
    public const string InvalidTraceContext = "invalid_trace_context";
    public const string InvalidHttp = "invalid_http";
    public const string OutcomeStatusMismatch = "outcome_status_mismatch";
    public const string InvalidClient = "invalid_client";
    public const string InvalidMetadata = "invalid_metadata";
    public const string MetadataKeyForbidden = "metadata_key_forbidden";
    public const string MetadataKeyNotAllowed = "metadata_key_not_allowed";
    public const string MetadataTooLarge = "metadata_too_large";
}

/// <summary>
/// Structural and taxonomy validation of the emitter payload (ADR114). It returns a stable,
/// payload-free error code; the caller maps it to a sanitized HTTP response in T17-C.
/// </summary>
public static class PlatformEventValidator
{
    private static readonly string[] ForbiddenMetadataFragments =
    [
        "password", "passwd", "secret", "token", "cookie", "authorization", "credential",
        "apikey", "embedding", "biometric", "image", "media", "video", "frame", "face", "rtsp"
    ];

    private static readonly IReadOnlySet<string> HttpMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"
    };

    public static string? Validate(
        PlatformEventV1? value, DateTimeOffset nowUtc, PlatformEventTypeCatalog? catalog = null)
    {
        if (value is null) return PlatformEventValidationErrors.InvalidEvent;
        if (!string.Equals(value.SchemaVersion, PlatformEventContract.SchemaVersion, StringComparison.Ordinal))
            return PlatformEventValidationErrors.UnsupportedSchemaVersion;
        if (value.EventId == Guid.Empty) return PlatformEventValidationErrors.InvalidEvent;
        if (!PlatformEventCategories.All.Contains(value.Category))
            return PlatformEventValidationErrors.InvalidCategory;
        if (!PlatformEventSeverities.All.Contains(value.Severity))
            return PlatformEventValidationErrors.InvalidSeverity;
        if (!PlatformEventOutcomes.All.Contains(value.Outcome))
            return PlatformEventValidationErrors.InvalidOutcome;
        if (!PlatformEventEmitters.All.Contains(value.Emitter))
            return PlatformEventValidationErrors.InvalidEmitter;
        if (!PlatformEventSources.IsValid(value.Source))
            return PlatformEventValidationErrors.InvalidSource;
        if (!ValidateOccurredAt(value.OccurredAtUtc, nowUtc))
            return PlatformEventValidationErrors.InvalidOccurredAt;
        if (value.Emitter == PlatformEventEmitters.Service && string.IsNullOrEmpty(value.SourceVersion))
            return PlatformEventValidationErrors.MissingSourceVersion;
        if (value.SourceVersion is not null && !IsSafeToken(value.SourceVersion, 64))
            return PlatformEventValidationErrors.InvalidSourceVersion;

        var actorError = ValidateActor(value.Actor);
        if (actorError is not null) return actorError;
        var subjectError = ValidateReference(value.Subject);
        if (subjectError is not null) return subjectError;
        var resourceError = ValidateReference(value.Resource);
        if (resourceError is not null) return resourceError;

        if (value.CorrelationId is not null && !IsSafeOpaqueId(
                value.CorrelationId, PlatformEventContract.MinimumCorrelationIdLength,
                PlatformEventContract.MaximumCorrelationIdLength))
            return PlatformEventValidationErrors.InvalidCorrelationId;
        if (value.CausationId is not null && !PlatformEventPublicId.IsValid(value.CausationId))
            return PlatformEventValidationErrors.InvalidCausationId;
        if (value.RequestId is not null && !IsSafeOpaqueId(value.RequestId, 1, PlatformEventContract.MaximumOpaqueIdLength))
            return PlatformEventValidationErrors.InvalidRequestId;
        if (value.SessionId is not null && !IsSafeOpaqueId(value.SessionId, 1, PlatformEventContract.MaximumOpaqueIdLength))
            return PlatformEventValidationErrors.InvalidSessionId;
        if (!ValidateTraceContext(value.TraceId, value.SpanId))
            return PlatformEventValidationErrors.InvalidTraceContext;

        var httpError = ValidateHttp(value.Http, value.Outcome);
        if (httpError is not null) return httpError;
        var clientError = ValidateClient(value.Client);
        if (clientError is not null) return clientError;

        IReadOnlyList<string> metadataAllowlist;
        if (string.Equals(value.Category, PlatformEventCategories.Legacy, StringComparison.Ordinal))
        {
            if (value.Source is not (PlatformEventSources.Legacy or PlatformEventSources.IamLegacy))
                return PlatformEventValidationErrors.InvalidSource;
            if (!string.Equals(value.Outcome, PlatformEventOutcomes.Unknown, StringComparison.Ordinal))
                return PlatformEventValidationErrors.InvalidOutcome;
            if (!IsSafeToken(value.EventType, 64))
                return PlatformEventValidationErrors.InvalidEventType;
            metadataAllowlist = [];
        }
        else
        {
            if (string.Equals(value.Outcome, PlatformEventOutcomes.Unknown, StringComparison.Ordinal))
                return PlatformEventValidationErrors.InvalidOutcome;
            if (!PlatformEventTypeCatalog.IsValidEventType(value.EventType))
                return PlatformEventValidationErrors.InvalidEventType;
            var activeCatalog = catalog ?? PlatformEventTypeCatalog.Default;
            if (!activeCatalog.TryGet(value.EventType, out var definition))
                return PlatformEventValidationErrors.UnknownEventType;
            if (!string.Equals(definition.Category, value.Category, StringComparison.Ordinal))
                return PlatformEventValidationErrors.EventTypeCategoryMismatch;
            metadataAllowlist = definition.MetadataKeys;
        }

        return ValidateMetadata(value.Metadata, metadataAllowlist);
    }

    private static bool ValidateOccurredAt(DateTimeOffset occurredAtUtc, DateTimeOffset nowUtc)
    {
        if (occurredAtUtc == default) return false;
        return occurredAtUtc.ToUniversalTime()
            <= nowUtc.ToUniversalTime().AddMinutes(PlatformEventContract.MaximumFutureSkewMinutes);
    }

    private static string? ValidateActor(PlatformEventActorV1? actor)
    {
        if (actor is null) return null;
        if (actor.UserPublicId is not null && !IsFormattedPublicId(actor.UserPublicId))
            return PlatformEventValidationErrors.InvalidActor;
        if (actor.Username is not null && !IsSafeText(actor.Username, 128))
            return PlatformEventValidationErrors.InvalidActor;
        if (actor.DisplayName is not null && !IsSafeText(actor.DisplayName, 128))
            return PlatformEventValidationErrors.InvalidActor;
        return null;
    }

    private static string? ValidateReference(PlatformEventReferenceV1? reference)
    {
        if (reference is null) return null;
        var type = reference.Type;
        var validType = type is { Length: > 0 and <= PlatformEventContract.MaximumReferenceTypeLength } &&
            char.IsAsciiLetterLower(type[0]) &&
            type.All(character => char.IsAsciiLetterLower(character)
                || char.IsAsciiDigit(character) || character is '_' or '.' or '-');
        if (!validType) return PlatformEventValidationErrors.InvalidReference;
        var publicId = reference.PublicId;
        var validId = publicId is { Length: > 0 and <= PlatformEventContract.MaximumReferenceIdLength } &&
            publicId.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');
        return validId ? null : PlatformEventValidationErrors.InvalidReference;
    }

    private static bool ValidateTraceContext(string? traceId, string? spanId)
    {
        if (traceId is null && spanId is null) return true;
        if (traceId is null || spanId is null) return false;
        return IsLowerHex(traceId, 32) && IsLowerHex(spanId, 16);
    }

    private static string? ValidateHttp(PlatformEventHttpV1? http, string outcome)
    {
        if (http is null) return null;
        if (!HttpMethods.Contains(http.Method)) return PlatformEventValidationErrors.InvalidHttp;
        var route = http.Route;
        if (route is not { Length: > 0 and <= PlatformEventContract.MaximumRouteLength } ||
            route[0] != '/' ||
            route.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)
                || character is '?' or '#'))
            return PlatformEventValidationErrors.InvalidHttp;
        if (http.StatusCode is < 100 or > 599 ||
            http.DurationMs is < 0 or > PlatformEventContract.MaximumDurationMs)
            return PlatformEventValidationErrors.InvalidHttp;
        return IsOutcomeCoherentWithStatus(outcome, http.StatusCode)
            ? null
            : PlatformEventValidationErrors.OutcomeStatusMismatch;
    }

    private static bool IsOutcomeCoherentWithStatus(string outcome, int statusCode) => statusCode switch
    {
        >= 200 and < 300 => string.Equals(outcome, PlatformEventOutcomes.Success, StringComparison.Ordinal),
        401 or 403 => string.Equals(outcome, PlatformEventOutcomes.Denied, StringComparison.Ordinal),
        >= 400 and < 500 => string.Equals(outcome, PlatformEventOutcomes.Failure, StringComparison.Ordinal),
        >= 500 and < 600 => string.Equals(outcome, PlatformEventOutcomes.Error, StringComparison.Ordinal),
        _ => outcome is PlatformEventOutcomes.Success or PlatformEventOutcomes.Unknown
    };

    private static string? ValidateClient(PlatformEventClientV1? client)
    {
        if (client is null) return null;
        if (client.UserAgent is not null &&
            !IsSafeText(client.UserAgent, PlatformEventContract.MaximumUserAgentLength))
            return PlatformEventValidationErrors.InvalidClient;
        if (client.Browser is not null &&
            !IsSafeText(client.Browser, PlatformEventContract.MaximumClientLabelLength))
            return PlatformEventValidationErrors.InvalidClient;
        if (client.Os is not null &&
            !IsSafeText(client.Os, PlatformEventContract.MaximumClientLabelLength))
            return PlatformEventValidationErrors.InvalidClient;
        if (client.Device is not null &&
            !IsSafeText(client.Device, PlatformEventContract.MaximumClientLabelLength))
            return PlatformEventValidationErrors.InvalidClient;
        if (client.Ip is not null && !IPAddress.TryParse(client.Ip, out _))
            return PlatformEventValidationErrors.InvalidClient;
        return null;
    }

    private static string? ValidateMetadata(
        IReadOnlyDictionary<string, object?>? metadata, IReadOnlyList<string> allowlist)
    {
        if (metadata is null || metadata.Count == 0) return null;
        if (metadata.Count > PlatformEventContract.MaximumMetadataKeys)
            return PlatformEventValidationErrors.MetadataTooLarge;

        foreach (var pair in metadata)
        {
            if (!IsSafeMetadataKey(pair.Key))
                return PlatformEventValidationErrors.InvalidMetadata;
            if (IsForbiddenMetadataKey(pair.Key))
                return PlatformEventValidationErrors.MetadataKeyForbidden;
            if (!allowlist.Contains(pair.Key, StringComparer.Ordinal))
                return PlatformEventValidationErrors.MetadataKeyNotAllowed;
            if (!IsSupportedMetadataValue(pair.Value))
                return PlatformEventValidationErrors.InvalidMetadata;
        }

        return PlatformEventCanonicalizer.SerializeMetadata(metadata).Length
            > PlatformEventContract.MaximumMetadataBytes
            ? PlatformEventValidationErrors.MetadataTooLarge
            : null;
    }

    private static bool IsSupportedMetadataValue(object? value) => value switch
    {
        null => true,
        string text => IsSafeText(text, 512),
        bool => true,
        int or long or decimal => true,
        double number => double.IsFinite(number),
        _ => false
    };

    private static bool IsSafeMetadataKey(string? key) =>
        key is { Length: > 0 and <= 64 } && char.IsAsciiLetter(key[0]) &&
        key.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');

    private static bool IsForbiddenMetadataKey(string key)
    {
        var normalized = new string(key.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        return ForbiddenMetadataFragments.Any(normalized.Contains);
    }

    private static bool IsSafeText(string? value, int maximumLength) =>
        value is { Length: > 0 } && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static bool IsSafeToken(string? value, int maximumLength) =>
        value is { Length: > 0 } && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsSafeOpaqueId(string? value, int minimumLength, int maximumLength) =>
        value is not null && value.Length >= minimumLength && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private static bool IsFormattedPublicId(string value)
    {
        var digitStart = value.Length - PlatformEventPublicId.Digits;
        if (digitStart is < 2 or > 4) return false;
        for (var index = 0; index < digitStart; index++)
        {
            if (!char.IsAsciiLetterUpper(value[index])) return false;
        }

        for (var index = digitStart; index < value.Length; index++)
        {
            if (!char.IsAsciiDigit(value[index])) return false;
        }

        return true;
    }

    private static bool IsLowerHex(string? value, int length) =>
        value is not null && value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
