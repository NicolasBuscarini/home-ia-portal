namespace GarageWatch.Application.Portal;

public static class PortalPreferenceCatalog
{
    public const string DefaultThemeId = "forest";
    public const string DefaultLanguageTag = "pt-BR";

    private static readonly string[] ThemeValues =
        ["forest", "graphite", "ocean", "light", "lavender", "sunset", "amber", "rose"];
    private static readonly string[] LanguageValues = ["pt-BR", "en-US"];

    public static IReadOnlyList<string> Themes => ThemeValues;
    public static IReadOnlyList<string> Languages => LanguageValues;

    public static bool IsSupportedTheme(string? value) =>
        value is not null && ThemeValues.Contains(value, StringComparer.Ordinal);

    public static bool IsSupportedLanguage(string? value) =>
        value is not null && LanguageValues.Contains(value, StringComparer.Ordinal);
}

public sealed record PortalModuleRecord(
    string ModuleId,
    string DisplayNameKey,
    string ContractVersion,
    string ApiBaseUrl,
    string? HealthPath,
    string? RequiredPermission,
    string MenuGroup,
    int MenuOrder,
    string MenuIcon,
    string MenuLabelKey,
    bool IsEnabled,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record PortalModulePage(IReadOnlyList<PortalModuleRecord> Items, int Page, int PageSize,
    int TotalCount);

public sealed record PortalModuleUpsert(
    string ModuleId,
    string DisplayNameKey,
    string ContractVersion,
    string ApiBaseUrl,
    string? HealthPath,
    string? RequiredPermission,
    string MenuGroup,
    int MenuOrder,
    string MenuIcon,
    string MenuLabelKey,
    bool IsEnabled);

public sealed record PortalNavigationModule(
    string ModuleId,
    string DisplayNameKey,
    string ContractVersion,
    string MenuGroup,
    int MenuOrder,
    string MenuIcon,
    string MenuLabelKey,
    string Route);

public sealed record PortalNavigation(
    string ResidenceId,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<PortalNavigationModule> Modules);

public sealed record PortalPreferences(
    string ThemeId,
    string LanguageTag,
    bool IsDefault,
    DateTimeOffset? UpdatedAtUtc,
    IReadOnlyList<string> SupportedThemes,
    IReadOnlyList<string> SupportedLanguages);

public sealed record PortalAuthorizationScope(string ContextId, string CameraId);

public sealed record PortalAuthorizationRequest(
    string Audience,
    string ResidenceId,
    IReadOnlyList<string> PermissionKeys,
    IReadOnlyList<PortalAuthorizationScope> Scopes);

public sealed record PortalPermissionDecision(
    string PermissionKey,
    bool Allowed,
    IReadOnlyList<PortalAuthorizationScope> AllowedScopes);

public sealed record PortalAuthorizationDecision(
    string SchemaVersion,
    string ResidenceId,
    string Audience,
    bool Allowed,
    IReadOnlyList<PortalPermissionDecision> Permissions,
    DateTimeOffset EvaluatedAtUtc);

public interface IPortalModuleCatalog
{
    Task<PortalModulePage> ListAsync(int page, int pageSize, CancellationToken cancellationToken = default);

    Task<PortalModuleRecord> CreateAsync(Guid actorUserId, PortalModuleUpsert request,
        CancellationToken cancellationToken = default);

    Task<PortalModuleRecord> UpdateAsync(Guid actorUserId, string moduleId, long expectedVersion,
        PortalModuleUpsert request, CancellationToken cancellationToken = default);
}

public interface IPortalNavigation
{
    Task<PortalNavigation> GetNavigationAsync(Guid userId, CancellationToken cancellationToken = default);
}

public interface IPortalPreferences
{
    Task<PortalPreferences> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<PortalPreferences> UpdatePreferencesAsync(Guid userId, string themeId, string languageTag,
        CancellationToken cancellationToken = default);
}

public interface IPortalAuthorizationDecisions
{
    Task<PortalAuthorizationDecision> DecideAsync(Guid userId, PortalAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class PortalValidationException(string message) : Exception(message);

public sealed class PortalNotFoundException(string message) : Exception(message);

public sealed class PortalConflictException(string message) : Exception(message);
