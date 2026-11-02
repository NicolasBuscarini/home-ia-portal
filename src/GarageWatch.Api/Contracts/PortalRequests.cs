namespace GarageWatch.Api.Contracts;

public sealed record CreatePortalModuleRequest(
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
    bool IsEnabled = true);

public sealed record UpdatePortalModuleRequest(
    long Version,
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

public sealed record UpdatePortalPreferencesRequest(string ThemeId, string LanguageTag);

public sealed record PortalAuthorizationScopeRequest(string ContextId, string CameraId);

public sealed record PortalAuthorizationDecisionRequest(
    string Audience,
    string ResidenceId,
    IReadOnlyList<string>? PermissionKeys,
    IReadOnlyList<PortalAuthorizationScopeRequest>? Scopes);
