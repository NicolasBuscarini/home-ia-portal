namespace GarageWatch.Application.PlatformEvents;

/// <summary>
/// Taxonomy entry for one stable <c>eventType</c>. Metadata keys are an allowlist: a key not listed
/// here is rejected instead of being silently accepted or masked.
/// </summary>
public sealed record PlatformEventTypeDefinition(
    string EventType,
    string Category,
    string DefaultSeverity,
    IReadOnlyList<string> MetadataKeys);

/// <summary>
/// Versioned catalog of event types owned by the platform. Event types are not free text; adding a
/// type or a metadata key requires a new catalog version and a contract test.
/// </summary>
public sealed class PlatformEventTypeCatalog
{
    public const string CurrentVersion = "1";

    private readonly IReadOnlyDictionary<string, PlatformEventTypeDefinition> definitions;

    static PlatformEventTypeCatalog()
    {
        Default = new PlatformEventTypeCatalog(CurrentVersion, DefaultDefinitions);
    }

    public PlatformEventTypeCatalog(string version, IEnumerable<PlatformEventTypeDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (string.IsNullOrWhiteSpace(version) || version.Length > 32)
            throw new ArgumentException("Versão de catálogo inválida.", nameof(version));

        var map = new Dictionary<string, PlatformEventTypeDefinition>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (definition is null || !IsValidEventType(definition.EventType))
                throw new ArgumentException("Tipo de evento inválido.", nameof(definitions));
            if (!PlatformEventCategories.All.Contains(definition.Category))
                throw new ArgumentException("Categoria de evento inválida.", nameof(definitions));
            if (!PlatformEventSeverities.All.Contains(definition.DefaultSeverity))
                throw new ArgumentException("Severidade padrão inválida.", nameof(definitions));
            if (definition.MetadataKeys is null
                || definition.MetadataKeys.Any(string.IsNullOrWhiteSpace)
                || definition.MetadataKeys.Distinct(StringComparer.Ordinal).Count() != definition.MetadataKeys.Count)
                throw new ArgumentException("Allowlist de metadados inválida.", nameof(definitions));
            if (!map.TryAdd(definition.EventType, definition))
                throw new ArgumentException("Tipo de evento duplicado.", nameof(definitions));
        }

        Version = version;
        this.definitions = map;
    }

    public static PlatformEventTypeCatalog Default { get; }

    public string Version { get; }

    public IEnumerable<PlatformEventTypeDefinition> Definitions => definitions.Values;

    public bool TryGet(string? eventType, out PlatformEventTypeDefinition definition)
    {
        if (eventType is not null && definitions.TryGetValue(eventType, out var found))
        {
            definition = found;
            return true;
        }

        definition = null!;
        return false;
    }

    /// <summary>Format of the stable dotted key: at least two lowercase segments.</summary>
    public static bool IsValidEventType(string? value)
    {
        if (value is not { Length: >= 3 and <= 64 }) return false;
        var segments = value.Split('.');
        return segments.Length >= 2 && segments.All(segment => segment.Length > 0 &&
            char.IsAsciiLetterLower(segment[0]) &&
            segment.All(character => char.IsAsciiLetterLower(character)
                || char.IsAsciiDigit(character) || character == '_'));
    }

    private static IReadOnlyList<PlatformEventTypeDefinition> DefaultDefinitions { get; } =
    [
        new("auth.login.succeeded", PlatformEventCategories.Authentication, PlatformEventSeverities.Info, []),
        new("auth.login.failed", PlatformEventCategories.Authentication, PlatformEventSeverities.Warning, []),
        new("auth.logout", PlatformEventCategories.Authentication, PlatformEventSeverities.Info, []),
        new("administrator.bootstrapped", PlatformEventCategories.Authentication,
            PlatformEventSeverities.Info, []),
        new("user.registered", PlatformEventCategories.Authentication, PlatformEventSeverities.Info, []),
        new("user.roles.updated", PlatformEventCategories.Administration, PlatformEventSeverities.Info, []),
        new("user.deactivated", PlatformEventCategories.Administration, PlatformEventSeverities.Warning, []),
        new("access.request.created", PlatformEventCategories.Administration,
            PlatformEventSeverities.Info, []),
        new("access.request.reviewed", PlatformEventCategories.Administration,
            PlatformEventSeverities.Info, []),
        new("access_profile.created", PlatformEventCategories.Administration,
            PlatformEventSeverities.Info, []),
        new("access_profile.version_published", PlatformEventCategories.Administration,
            PlatformEventSeverities.Info, []),
        new("access_profiles.assigned", PlatformEventCategories.Administration,
            PlatformEventSeverities.Info, []),
        new("camera_context.created", PlatformEventCategories.Administration,
            PlatformEventSeverities.Info, []),
        new("camera_context.updated", PlatformEventCategories.Administration,
            PlatformEventSeverities.Info, []),
        new("camera_context.deactivated", PlatformEventCategories.Administration,
            PlatformEventSeverities.Warning, []),
        new("camera_catalog.created", PlatformEventCategories.Administration,
            PlatformEventSeverities.Info, []),
        new("camera_catalog.updated", PlatformEventCategories.Administration,
            PlatformEventSeverities.Info, []),
        new("camera_catalog.deactivated", PlatformEventCategories.Administration,
            PlatformEventSeverities.Warning, []),
        new("camera.connection.address.updated", PlatformEventCategories.Administration,
            PlatformEventSeverities.Info, []),
        new("known_person.created", PlatformEventCategories.Administration,
            PlatformEventSeverities.Info, []),
        new("roster.person.created", PlatformEventCategories.Administration,
            PlatformEventSeverities.Info, []),
        new("roster.person.removed", PlatformEventCategories.Administration,
            PlatformEventSeverities.Warning, []),
        new("known_person.removed", PlatformEventCategories.Administration,
            PlatformEventSeverities.Warning, []),
        new("portal_module.created", PlatformEventCategories.Administration,
            PlatformEventSeverities.Info, []),
        new("portal_module.updated", PlatformEventCategories.Administration,
            PlatformEventSeverities.Info, []),
        new("portal_module.disabled", PlatformEventCategories.Administration,
            PlatformEventSeverities.Warning, []),
        new("face_extractor.candidate_created", PlatformEventCategories.Administration,
            PlatformEventSeverities.Info, []),
        new("face_extractor.selected_for_evaluation", PlatformEventCategories.Administration,
            PlatformEventSeverities.Info, []),
        new("camera.access.granted", PlatformEventCategories.Authorization,
            PlatformEventSeverities.Info, []),
        new("camera.access.revoked", PlatformEventCategories.Authorization,
            PlatformEventSeverities.Warning, []),
        new("camera.access.denied", PlatformEventCategories.Authorization,
            PlatformEventSeverities.Warning, []),
        new("activitywatch.read.denied", PlatformEventCategories.Authorization,
            PlatformEventSeverities.Warning, []),
        new("activitywatch.read", PlatformEventCategories.Integration,
            PlatformEventSeverities.Info, []),
        new("activitywatch.read.unavailable", PlatformEventCategories.Integration,
            PlatformEventSeverities.Warning, []),
        new("clip.analysis.request.denied", PlatformEventCategories.Authorization,
            PlatformEventSeverities.Warning, []),
        new("clip.analysis.request.accepted", PlatformEventCategories.Media,
            PlatformEventSeverities.Info, []),
        new("clip.deleted", PlatformEventCategories.Media,
            PlatformEventSeverities.Warning, []),
        new("clip.delete.all", PlatformEventCategories.Media,
            PlatformEventSeverities.Warning,
            ["deleted", "skippedPreserved", "skippedArchived", "skippedNotReady", "skippedActiveAnalysis", "failed"]),
        new("clip.delete.denied", PlatformEventCategories.Authorization,
            PlatformEventSeverities.Warning, []),
        new("biometric.enrollment_created", PlatformEventCategories.Biometrics,
            PlatformEventSeverities.Info, []),
        new("biometric.enrollment_revoked", PlatformEventCategories.Biometrics,
            PlatformEventSeverities.Warning, []),
        new("biometric.enrollment_expired", PlatformEventCategories.Biometrics,
            PlatformEventSeverities.Info, []),
        new("biometric.person_linked", PlatformEventCategories.Biometrics,
            PlatformEventSeverities.Info, []),
        new("biometric.person_unlinked", PlatformEventCategories.Biometrics,
            PlatformEventSeverities.Warning, []),
        new("identity.observation.reviewed", PlatformEventCategories.Biometrics,
            PlatformEventSeverities.Info, ["decision", "matchStatus", "policyVersion", "personPublicId"]),
        new("identity.match_policy.drafted", PlatformEventCategories.Biometrics,
            PlatformEventSeverities.Info, ["threshold", "margin", "genuineCount", "impostorCount"]),
        new("identity.match_policy.promoted", PlatformEventCategories.Biometrics,
            PlatformEventSeverities.Warning,
            ["version", "previousVersion", "threshold", "margin", "rollback"]),
        new("portal.preferences.updated", PlatformEventCategories.Configuration,
            PlatformEventSeverities.Info, []),
        new("monitoring.capture.settings.updated", PlatformEventCategories.Configuration,
            PlatformEventSeverities.Info, []),
        new("clip.analysis.policy.updated", PlatformEventCategories.Configuration,
            PlatformEventSeverities.Info, []),
        new("clip.trigger.configuration.updated", PlatformEventCategories.Configuration,
            PlatformEventSeverities.Info, []),
        new("platform.settings.updated", PlatformEventCategories.Configuration,
            PlatformEventSeverities.Info, []),
        new("clip.preserved", PlatformEventCategories.Media, PlatformEventSeverities.Info,
            ["isPreserved", "policyVersion"]),
        new("clip.preservation.enabled", PlatformEventCategories.Media,
            PlatformEventSeverities.Info, []),
        new("clip.preservation.disabled", PlatformEventCategories.Media,
            PlatformEventSeverities.Info, []),
        new("clip.retention.policy.updated", PlatformEventCategories.Media,
            PlatformEventSeverities.Info, []),
        new("clip.retention.expired", PlatformEventCategories.Media,
            PlatformEventSeverities.Info, []),
        new("integration_credential.updated", PlatformEventCategories.Integration,
            PlatformEventSeverities.Info, []),
        new("integration_credential.removed", PlatformEventCategories.Integration,
            PlatformEventSeverities.Warning, []),
        new("integration_credential.rotated", PlatformEventCategories.Integration,
            PlatformEventSeverities.Info, []),
        new("navigation.viewed", PlatformEventCategories.Navigation, PlatformEventSeverities.Info, []),
        new("ui.action", PlatformEventCategories.Navigation, PlatformEventSeverities.Info, ["action"]),
        new("event_store.queried", PlatformEventCategories.Administration,
            PlatformEventSeverities.Info, ["pageSize", "resultCount", "hasCursor"]),
        new("event_store.viewed", PlatformEventCategories.Administration,
            PlatformEventSeverities.Info, [])
    ];
}
