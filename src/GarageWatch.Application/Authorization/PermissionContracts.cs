namespace GarageWatch.Application.Authorization;

public static class PermissionCatalog
{
    public const string Version = "2.0";

    private static readonly IReadOnlyDictionary<string, PermissionDefinition> Entries =
        BuildEntries();

    private static IReadOnlyDictionary<string, PermissionDefinition> BuildEntries()
    {
        static IReadOnlySet<PermissionScopeKind> Kinds(params PermissionScopeKind[] kinds) => kinds.ToHashSet();
        var all = Kinds(PermissionScopeKind.Global, PermissionScopeKind.Module,
            PermissionScopeKind.Context, PermissionScopeKind.Camera);
        var globalModule = Kinds(PermissionScopeKind.Global, PermissionScopeKind.Module);
        var entries = new[]
        {
            new PermissionDefinition("camera.list", "List authorized cameras", "cameras", "read",
                PermissionRisk.Low, all),
            new PermissionDefinition("live.video.view", "View live video", "monitoring", "read",
                PermissionRisk.Low, all),
            new PermissionDefinition("live.audio.listen", "Listen to live audio", "monitoring", "read",
                PermissionRisk.Medium, all, IsAvailable: false,
                UnavailableReason: "Aguardando rendição somente vídeo no servidor."),
            new PermissionDefinition("events.read", "Read events", "events", "read",
                PermissionRisk.Low, all),
            new PermissionDefinition("clips.read", "Read clips", "clips", "read",
                PermissionRisk.Low, all),
            new PermissionDefinition("clips.export", "Export clips", "clips", "read",
                PermissionRisk.Medium, all),
            new PermissionDefinition("clips.delete", "Delete clips", "clips", "delete",
                PermissionRisk.High, all),
            new PermissionDefinition("clips.analysis.request", "Request clip analysis", "clips", "analysis",
                PermissionRisk.Low, all),
            new PermissionDefinition("clips.analysis.policy.manage", "Manage clip analysis dispatch policy",
                "clips", "analysis", PermissionRisk.High, all),
            new PermissionDefinition("activitywatch.read", "Read external activity summaries", "activitywatch",
                "read", PermissionRisk.Low, globalModule),
            new PermissionDefinition("biometrics.manage", "Manage biometric enrollments of other accounts",
                "biometrics", "write", PermissionRisk.High, globalModule),
            new PermissionDefinition("admin.cameras.read", "Read administrative camera catalog", "administration",
                "cameras", PermissionRisk.Low, globalModule),
            new PermissionDefinition("admin.cameras.manage", "Manage cameras, contexts and connections",
                "administration", "cameras", PermissionRisk.High, globalModule),
            new PermissionDefinition("admin.users.read", "Read accounts and assignments", "administration",
                "users", PermissionRisk.Medium, globalModule),
            new PermissionDefinition("admin.users.manage", "Manage accounts and access assignments",
                "administration", "users", PermissionRisk.High, globalModule),
            new PermissionDefinition("admin.permissions.read", "Read the permission catalog and profiles",
                "administration", "permissions", PermissionRisk.Medium, globalModule),
            new PermissionDefinition("admin.permissions.manage", "Manage profiles and permission catalog",
                "administration", "permissions", PermissionRisk.Critical, globalModule, OwnerOnly: true),
            new PermissionDefinition("admin.modules.read", "Read the portal module catalog", "administration",
                "modules", PermissionRisk.Low, globalModule),
            new PermissionDefinition("admin.modules.manage", "Manage the portal module catalog", "administration",
                "modules", PermissionRisk.High, globalModule),
            new PermissionDefinition("admin.integrations.manage", "Manage external integration credentials",
                "administration", "integrations", PermissionRisk.Critical, globalModule),
            new PermissionDefinition("admin.requests.read", "Read access requests", "administration", "requests",
                PermissionRisk.Medium, globalModule),
            new PermissionDefinition("admin.requests.manage", "Review access requests", "administration",
                "requests", PermissionRisk.High, globalModule),
            new PermissionDefinition("admin.people.read", "Read people and biometric links", "administration",
                "people", PermissionRisk.Medium, globalModule),
            new PermissionDefinition("admin.people.manage", "Manage people and biometric links", "administration",
                "people", PermissionRisk.High, globalModule),
            new PermissionDefinition("admin.capture.manage", "Manage capture and clip trigger settings",
                "administration", "capture", PermissionRisk.High, globalModule),
            new PermissionDefinition("admin.retention.manage", "Manage clip retention and preservation",
                "administration", "retention", PermissionRisk.High, globalModule),
            new PermissionDefinition("admin.models.read", "Read model and extractor registry", "administration",
                "models", PermissionRisk.Medium, globalModule),
            new PermissionDefinition("admin.models.manage", "Manage extractor candidates and selection",
                "administration", "models", PermissionRisk.High, globalModule),
            new PermissionDefinition("admin.models.promote", "Promote evaluated models", "administration",
                "models", PermissionRisk.Critical, globalModule),
            new PermissionDefinition("admin.configuration.manage", "Manage privileged configuration",
                "administration", "configuration", PermissionRisk.High, globalModule),
            new PermissionDefinition("admin.system.health.read", "Read detailed system health", "administration",
                "health", PermissionRisk.Medium, globalModule),
            new PermissionDefinition("system.owner", "Portal ownership and owner management", "system", "owner",
                PermissionRisk.Critical, Kinds(PermissionScopeKind.Global), OwnerOnly: true)
        };
        return entries.ToDictionary(item => item.Key, StringComparer.Ordinal);
    }

    private static readonly IReadOnlyCollection<PermissionDefinition> PublicEntries =
        Array.AsReadOnly(Entries.Values.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray());

    public static IReadOnlyCollection<PermissionDefinition> All => PublicEntries;

    public static IReadOnlyCollection<string> Modules =>
        PublicEntries.Select(item => item.Module).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public static bool TryGet(string key, out PermissionDefinition definition) => Entries.TryGetValue(key, out definition!);
}

public enum PermissionScopeKind
{
    Global,
    Module,
    Context,
    Camera
}

public enum PermissionRisk
{
    Low,
    Medium,
    High,
    Critical
}

public sealed record PermissionDefinition(string Key, string Description, string Module, string Category,
    PermissionRisk Risk, IReadOnlySet<PermissionScopeKind> AllowedScopeKinds, bool OwnerOnly = false,
    bool IsAvailable = true, string? UnavailableReason = null)
{
    public bool IsDelegable => !OwnerOnly && IsAvailable;
}

public sealed record PermissionScope(PermissionScopeKind Kind, string? ContextId = null,
    string? CameraId = null, string? ModuleId = null)
{
    public static PermissionScope Global => new(PermissionScopeKind.Global);
    public static PermissionScope ForModule(string moduleId) => new(PermissionScopeKind.Module, ModuleId: moduleId);
    public static PermissionScope ForContext(string contextId) => new(PermissionScopeKind.Context, contextId);
    public static PermissionScope ForCamera(string contextId, string cameraId) =>
        new(PermissionScopeKind.Camera, contextId, cameraId);
}

public sealed record PermissionGrant(string PermissionKey, IReadOnlySet<PermissionScope> Scopes);

public sealed record AuthorizationSubject(bool IsActive, IReadOnlySet<string> Roles,
    IReadOnlyCollection<PermissionGrant> Grants, bool IsOwner = false);

public sealed record PermissionRequest(string PermissionKey, string? ContextId = null, string? CameraId = null,
    string? ModuleId = null)
{
    public PermissionScope Target =>
        !string.IsNullOrWhiteSpace(ContextId) && !string.IsNullOrWhiteSpace(CameraId)
            ? PermissionScope.ForCamera(ContextId, CameraId)
            : !string.IsNullOrWhiteSpace(ContextId)
                ? PermissionScope.ForContext(ContextId)
                : !string.IsNullOrWhiteSpace(ModuleId)
                    ? PermissionScope.ForModule(ModuleId)
                    : PermissionScope.Global;
}

public interface IPermissionEvaluator
{
    bool IsAllowed(AuthorizationSubject subject, PermissionRequest request);
}

public sealed class PermissionEvaluator : IPermissionEvaluator
{
    public bool IsAllowed(AuthorizationSubject subject, PermissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(request);

        if (!subject.IsActive || !PermissionCatalog.TryGet(request.PermissionKey, out var definition))
            return false;

        if (!definition.IsAvailable)
            return false;

        if (subject.IsOwner)
            return true;

        if (!definition.IsDelegable)
            return false;

        var target = request.Target;
        if (!definition.AllowedScopeKinds.Contains(target.Kind))
            return false;

        return subject.Grants.Any(grant =>
            StringComparer.Ordinal.Equals(grant.PermissionKey, definition.Key) &&
            grant.Scopes.Any(scope => Covers(scope, target, definition.Module)));
    }

    private static bool Covers(PermissionScope grant, PermissionScope target, string module) =>
        grant.Kind switch
        {
            PermissionScopeKind.Global => true,
            PermissionScopeKind.Module => string.Equals(grant.ModuleId, target.ModuleId ?? module, StringComparison.Ordinal),
            PermissionScopeKind.Context => target.Kind is PermissionScopeKind.Context or PermissionScopeKind.Camera
                && string.Equals(grant.ContextId, target.ContextId, StringComparison.Ordinal),
            PermissionScopeKind.Camera => target.Kind == PermissionScopeKind.Camera
                && string.Equals(grant.ContextId, target.ContextId, StringComparison.Ordinal)
                && string.Equals(grant.CameraId, target.CameraId, StringComparison.Ordinal),
            _ => false
        };
}

public static class PermissionPolicy
{
    public static string Name(string permissionKey) => $"permission:{permissionKey}";
}
