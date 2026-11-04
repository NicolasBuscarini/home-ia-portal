using System.Text.Json;
using GarageWatch.Application.PlatformEvents;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class PlatformEventContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 18, 23, 0, TimeSpan.Zero);

    [Fact]
    public async Task Example_fixture_parses_validates_and_matches_schema_and_hash()
    {
        using var schema = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "contracts", "platform-event.v1.schema.json")));
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "contracts", "platform-event.v1.example.json")));
        var root = fixture.RootElement;

        var value = ReadEvent(root);

        Assert.Null(PlatformEventValidator.Validate(value, Now));
        Assert.Equal(root.GetProperty("payloadHash").GetString(),
            PlatformEventCanonicalizer.ComputePayloadHash(value));
        Assert.True(PlatformEventPublicId.IsValid(root.GetProperty("publicId").GetString()));
        Assert.True(root.GetProperty("recordedAtUtc").GetDateTimeOffset()
            >= root.GetProperty("occurredAtUtc").GetDateTimeOffset());

        var required = schema.RootElement.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()!).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(PlatformEventContract.StoredFieldNames.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            required);
        Assert.Equal(PlatformEventCategories.All.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            ReadEnum(schema, "category").OrderBy(item => item, StringComparer.Ordinal).ToArray());
        Assert.Equal(PlatformEventSeverities.All.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            ReadEnum(schema, "severity").OrderBy(item => item, StringComparer.Ordinal).ToArray());
        Assert.Equal(PlatformEventOutcomes.All.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            ReadEnum(schema, "outcome").OrderBy(item => item, StringComparer.Ordinal).ToArray());
        Assert.Equal(PlatformEventEmitters.All.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            ReadEnum(schema, "emitter").OrderBy(item => item, StringComparer.Ordinal).ToArray());
        Assert.Equal(20, schema.RootElement.GetProperty("properties").GetProperty("metadata")
            .GetProperty("maxProperties").GetInt32());
    }

    [Fact]
    public void Validator_rejects_unknown_event_type_and_category_mismatch()
    {
        Assert.Equal(PlatformEventValidationErrors.UnknownEventType,
            PlatformEventValidator.Validate(ValidEvent() with { EventType = "unknown.type" }, Now));
        Assert.Equal(PlatformEventValidationErrors.EventTypeCategoryMismatch,
            PlatformEventValidator.Validate(
                ValidEvent() with { Category = PlatformEventCategories.Administration }, Now));
        Assert.Equal(PlatformEventValidationErrors.InvalidEventType,
            PlatformEventValidator.Validate(ValidEvent() with { EventType = "Clip Preserved" }, Now));
    }

    [Fact]
    public void Validator_bounds_occurred_at_and_requires_emitter_time()
    {
        Assert.Equal(PlatformEventValidationErrors.InvalidOccurredAt,
            PlatformEventValidator.Validate(ValidEvent() with { OccurredAtUtc = default }, Now));
        Assert.Equal(PlatformEventValidationErrors.InvalidOccurredAt,
            PlatformEventValidator.Validate(
                ValidEvent() with { OccurredAtUtc = Now.AddMinutes(6) }, Now));
        Assert.Null(PlatformEventValidator.Validate(
            ValidEvent() with { OccurredAtUtc = Now.AddMinutes(4) }, Now));
    }

    [Fact]
    public void Metadata_is_allowlisted_by_event_type_and_forbidden_keys_fail()
    {
        Assert.Equal(PlatformEventValidationErrors.MetadataKeyForbidden,
            PlatformEventValidator.Validate(ValidEvent() with
            {
                Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["isPreserved"] = true,
                    ["userPassword"] = "secret"
                }
            }, Now));
        Assert.Equal(PlatformEventValidationErrors.MetadataKeyNotAllowed,
            PlatformEventValidator.Validate(ValidEvent() with
            {
                Metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { ["unexpected"] = true }
            }, Now));
        Assert.Equal(PlatformEventValidationErrors.InvalidMetadata,
            PlatformEventValidator.Validate(ValidEvent() with
            {
                Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["isPreserved"] = new object()
                }
            }, Now));
    }

    [Fact]
    public void Metadata_is_bounded_by_key_count_and_serialized_size()
    {
        var tooManyKeys = Enumerable.Range(0, 21)
            .ToDictionary(index => $"key{index}", _ => (object?)true, StringComparer.Ordinal);
        Assert.Equal(PlatformEventValidationErrors.MetadataTooLarge,
            PlatformEventValidator.Validate(ValidEvent() with { Metadata = tooManyKeys }, Now));

        var keys = Enumerable.Range(0, 20).Select(index => $"key{index}").ToArray();
        var catalog = new PlatformEventTypeCatalog("test", [
            new PlatformEventTypeDefinition("test.metadata.large", PlatformEventCategories.Media,
                PlatformEventSeverities.Info, keys)
        ]);
        var oversized = keys.ToDictionary(key => key, _ => (object?)new string('x', 500),
            StringComparer.Ordinal);
        var value = ValidEvent() with
        {
            EventType = "test.metadata.large",
            Category = PlatformEventCategories.Media,
            Metadata = oversized
        };
        Assert.Equal(PlatformEventValidationErrors.MetadataTooLarge,
            PlatformEventValidator.Validate(value, Now, catalog));
    }

    [Fact]
    public void Outcome_must_match_the_http_status()
    {
        Assert.Equal(PlatformEventValidationErrors.OutcomeStatusMismatch,
            PlatformEventValidator.Validate(ValidEvent() with
            {
                Http = new PlatformEventHttpV1("PUT", "/api/v1/clips/{clipId}", 500, 12)
            }, Now));
        Assert.Equal(PlatformEventValidationErrors.OutcomeStatusMismatch,
            PlatformEventValidator.Validate(ValidEvent() with
            {
                Outcome = PlatformEventOutcomes.Denied
            }, Now));
        Assert.Null(PlatformEventValidator.Validate(ValidEvent() with
        {
            Outcome = PlatformEventOutcomes.Denied,
            Http = new PlatformEventHttpV1("PUT", "/api/v1/clips/{clipId}", 403, 12)
        }, Now));
    }

    [Fact]
    public void Service_events_require_a_source_version()
    {
        Assert.Equal(PlatformEventValidationErrors.MissingSourceVersion,
            PlatformEventValidator.Validate(ValidEvent() with { SourceVersion = null }, Now));
        Assert.Null(PlatformEventValidator.Validate(
            ValidEvent() with { Emitter = PlatformEventEmitters.Browser, SourceVersion = null }, Now));
    }

    [Fact]
    public void Correlation_reference_and_trace_context_are_validated()
    {
        Assert.Equal(PlatformEventValidationErrors.InvalidCorrelationId,
            PlatformEventValidator.Validate(ValidEvent() with { CorrelationId = "short" }, Now));
        Assert.Equal(PlatformEventValidationErrors.InvalidCausationId,
            PlatformEventValidator.Validate(ValidEvent() with { CausationId = "EVT1" }, Now));
        Assert.Equal(PlatformEventValidationErrors.InvalidReference,
            PlatformEventValidator.Validate(ValidEvent() with
            {
                Resource = new PlatformEventReferenceV1("clip", "CLP 000000000123")
            }, Now));
        Assert.Equal(PlatformEventValidationErrors.InvalidTraceContext,
            PlatformEventValidator.Validate(ValidEvent() with { SpanId = null }, Now));
        Assert.Equal(PlatformEventValidationErrors.InvalidClient,
            PlatformEventValidator.Validate(ValidEvent() with
            {
                Client = new PlatformEventClientV1("Mozilla/5.0", "Firefox", "Linux", "desktop", "not-an-ip")
            }, Now));
    }

    [Fact]
    public void Legacy_backfill_envelope_is_accepted_without_catalog_entry()
    {
        var legacy = ValidEvent() with
        {
            EventType = "camera.access.granted",
            Category = PlatformEventCategories.Legacy,
            Outcome = PlatformEventOutcomes.Unknown,
            Source = PlatformEventSources.IamLegacy,
            Http = null,
            Metadata = null
        };

        Assert.Null(PlatformEventValidator.Validate(legacy, Now));
        Assert.Null(PlatformEventValidator.Validate(legacy with { Source = PlatformEventSources.Portal }, Now));
        Assert.Equal(PlatformEventValidationErrors.InvalidOutcome,
            PlatformEventValidator.Validate(legacy with { Outcome = PlatformEventOutcomes.Success }, Now));
        Assert.Equal(PlatformEventValidationErrors.MetadataKeyNotAllowed,
            PlatformEventValidator.Validate(legacy with
            {
                Metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { ["unexpected"] = true }
            }, Now));
    }

    [Fact]
    public void Canonical_hash_ignores_metadata_order_and_detects_tampering()
    {
        var first = ValidEvent() with
        {
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["isPreserved"] = true,
                ["policyVersion"] = 4
            }
        };
        var reordered = first with
        {
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["policyVersion"] = 4,
                ["isPreserved"] = true
            }
        };

        Assert.Equal(PlatformEventCanonicalizer.ComputePayloadHash(first),
            PlatformEventCanonicalizer.ComputePayloadHash(reordered));
        Assert.NotEqual(PlatformEventCanonicalizer.ComputePayloadHash(first),
            PlatformEventCanonicalizer.ComputePayloadHash(first with
            {
                Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["isPreserved"] = false,
                    ["policyVersion"] = 4
                }
            }));
        Assert.NotEqual(PlatformEventCanonicalizer.ComputePayloadHash(first),
            PlatformEventCanonicalizer.ComputePayloadHash(first with { EventId = Guid.NewGuid() }));
    }

    [Fact]
    public void Catalog_rejects_duplicate_types_and_unknown_taxonomy()
    {
        var definition = new PlatformEventTypeDefinition(
            "test.valid", PlatformEventCategories.System, PlatformEventSeverities.Info, []);

        _ = new PlatformEventTypeCatalog("test", [definition]);
        Assert.Throws<ArgumentException>(() =>
            new PlatformEventTypeCatalog("test", [definition, definition]));
        Assert.Throws<ArgumentException>(() =>
            new PlatformEventTypeCatalog("test", [definition with { Category = "unsupported" }]));
        Assert.Throws<ArgumentException>(() =>
            new PlatformEventTypeCatalog("test", [definition with { MetadataKeys = ["a", "a"] }]));
        Assert.False(PlatformEventTypeCatalog.IsValidEventType("onlyoneword"));
        Assert.False(PlatformEventTypeCatalog.IsValidEventType("Upper.case"));
        Assert.True(PlatformEventTypeCatalog.IsValidEventType("camera_context.created"));
    }

    [Fact]
    public void Public_id_and_record_creation_are_bounded()
    {
        Assert.True(PlatformEventPublicId.IsValid("EVT000000000123"));
        Assert.False(PlatformEventPublicId.IsValid("EVT123"));
        Assert.False(PlatformEventPublicId.IsValid("evt000000000123"));
        Assert.False(PlatformEventPublicId.IsValid("EVT00000000012x"));

        Assert.Throws<ArgumentException>(() =>
            PlatformEventCanonicalizer.CreateRecord(ValidEvent(), "EVT1", Now));
        var record = PlatformEventCanonicalizer.CreateRecord(
            ValidEvent(), "EVT000000000123",
            Now.ToOffset(TimeSpan.FromHours(-3)));
        Assert.Equal(PlatformEventCanonicalizer.ComputePayloadHash(ValidEvent()), record.PayloadHash);
        Assert.Equal(TimeSpan.Zero, record.RecordedAtUtc.Offset);
    }

    private static IReadOnlyList<string> ReadEnum(JsonDocument schema, string property) =>
        schema.RootElement.GetProperty("properties").GetProperty(property).GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static PlatformEventV1 ValidEvent() => new(
        Guid.Parse("e0f0b6a2-7f6e-4f3a-9d4f-2a1b3c4d5e6f"),
        "clip.preserved",
        PlatformEventCategories.Media,
        PlatformEventSeverities.Info,
        PlatformEventOutcomes.Success,
        Now.AddSeconds(-10),
        PlatformEventSources.Monitoring,
        "1.0",
        PlatformEventEmitters.Service,
        new PlatformEventActorV1("USR000000000042", null, null),
        new PlatformEventReferenceV1("clip", "CLP000000000123"),
        new PlatformEventReferenceV1("clip", "CLP000000000123"),
        "c7f4b4a1-2f6e-4b0e-9a1d-5b8e2f0c3d11",
        "EVT000000000456",
        "0HN7G5A2B3C4D:00000001",
        null,
        "4bf92f3577b34da6a3ce929d0e0e4736",
        "00f067aa0ba902b7",
        new PlatformEventHttpV1("PUT", "/api/v1/clips/{clipId}/preservation", 200, 41),
        new PlatformEventClientV1("Mozilla/5.0 (X11; Linux x86_64)", "Firefox", "Linux", "desktop",
            "127.0.0.1"),
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["isPreserved"] = true,
            ["policyVersion"] = 4
        });

    private static PlatformEventV1 ReadEvent(JsonElement root) => new(
        root.GetProperty("eventId").GetGuid(),
        root.GetProperty("eventType").GetString()!,
        root.GetProperty("category").GetString()!,
        root.GetProperty("severity").GetString()!,
        root.GetProperty("outcome").GetString()!,
        root.GetProperty("occurredAtUtc").GetDateTimeOffset(),
        root.GetProperty("source").GetString()!,
        root.GetProperty("sourceVersion").GetString(),
        root.GetProperty("emitter").GetString()!,
        ReadActor(root.GetProperty("actor")),
        ReadReference(root.GetProperty("subject")),
        ReadReference(root.GetProperty("resource")),
        root.GetProperty("correlationId").GetString(),
        root.GetProperty("causationId").GetString(),
        root.GetProperty("requestId").GetString(),
        root.GetProperty("sessionId").GetString(),
        root.GetProperty("traceId").GetString(),
        root.GetProperty("spanId").GetString(),
        ReadHttp(root.GetProperty("http")),
        ReadClient(root.GetProperty("client")),
        ReadMetadata(root.GetProperty("metadata")))
    {
        SchemaVersion = root.GetProperty("schemaVersion").GetString()!
    };

    private static PlatformEventActorV1? ReadActor(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null
            ? null
            : new PlatformEventActorV1(element.GetProperty("userPublicId").GetString(),
                element.GetProperty("username").GetString(),
                element.GetProperty("displayName").GetString());

    private static PlatformEventReferenceV1? ReadReference(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null
            ? null
            : new PlatformEventReferenceV1(element.GetProperty("type").GetString()!,
                element.GetProperty("publicId").GetString()!);

    private static PlatformEventHttpV1? ReadHttp(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null
            ? null
            : new PlatformEventHttpV1(element.GetProperty("method").GetString()!,
                element.GetProperty("route").GetString()!,
                element.GetProperty("statusCode").GetInt32(),
                element.GetProperty("durationMs").GetInt64());

    private static PlatformEventClientV1? ReadClient(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null
            ? null
            : new PlatformEventClientV1(element.GetProperty("userAgent").GetString(),
                element.GetProperty("browser").GetString(),
                element.GetProperty("os").GetString(),
                element.GetProperty("device").GetString(),
                element.GetProperty("ip").GetString());

    private static IReadOnlyDictionary<string, object?> ReadMetadata(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (element.ValueKind == JsonValueKind.Null) return result;
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => property.Value.TryGetInt64(out var number)
                    ? number
                    : property.Value.GetDouble(),
                JsonValueKind.Null => null,
                _ => throw new InvalidOperationException("Metadado não suportado na fixture.")
            };
        }

        return result;
    }
}
