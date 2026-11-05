using GarageWatch.Application.Authorization;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class PermissionEvaluatorTests
{
    private readonly PermissionEvaluator evaluator = new();

    [Fact]
    public void Catalog_is_finite_versioned_and_marks_owner_only_and_unavailable_capabilities()
    {
        Assert.Equal("2.0", PermissionCatalog.Version);
        Assert.Equal(PermissionCatalog.All.Count, PermissionCatalog.All.Select(entry => entry.Key)
            .Distinct(StringComparer.Ordinal).Count());
        Assert.True(PermissionCatalog.TryGet("live.video.view", out var video));
        Assert.True(video.IsDelegable);
        Assert.True(PermissionCatalog.TryGet("live.audio.listen", out var audio));
        Assert.False(audio.IsDelegable);
        Assert.False(audio.IsAvailable);
        Assert.NotNull(audio.UnavailableReason);
        Assert.True(PermissionCatalog.TryGet("admin.users.manage", out var admin));
        Assert.True(admin.IsDelegable);
        Assert.True(PermissionCatalog.TryGet("system.owner", out var owner));
        Assert.True(owner.OwnerOnly);
        Assert.False(owner.IsDelegable);
        Assert.True(PermissionCatalog.TryGet("biometrics.manage", out var biometrics));
        Assert.True(biometrics.IsDelegable);
    }

    [Fact]
    public void Owner_is_explicit_and_does_not_depend_on_an_invisible_role_bypass()
    {
        var owner = Subject(true, ["Administrator"], owner: true);

        Assert.True(evaluator.IsAllowed(owner, new("admin.users.manage", ModuleId: "administration")));
        Assert.True(evaluator.IsAllowed(owner, new("live.video.view")));
        Assert.False(evaluator.IsAllowed(owner, new("admin.unknown")));
        Assert.False(evaluator.IsAllowed(Subject(false, ["Administrator"], owner: true), new("live.video.view")));
        Assert.False(evaluator.IsAllowed(Subject(true, ["Administrator"]), new("admin.users.manage")));
    }

    [Fact]
    public void Scope_coverage_supports_global_module_context_and_camera()
    {
        var subject = Subject(true, ["User"],
        [
            new("live.video.view", Set(PermissionScope.ForCamera("garage", "garage-01"))),
            new("clips.read", Set(PermissionScope.ForContext("garage"))),
            new("admin.cameras.read", Set(PermissionScope.ForModule("administration"))),
            new("biometrics.manage", Set(PermissionScope.Global))
        ]);

        Assert.True(evaluator.IsAllowed(subject, new("live.video.view", "garage", "garage-01")));
        Assert.False(evaluator.IsAllowed(subject, new("live.video.view", "garage", "garage-02")));
        Assert.True(evaluator.IsAllowed(subject, new("clips.read", "garage", "garage-09")));
        Assert.False(evaluator.IsAllowed(subject, new("clips.read", "other", "garage-01")));
        Assert.True(evaluator.IsAllowed(subject, new("admin.cameras.read", ModuleId: "administration")));
        Assert.False(evaluator.IsAllowed(subject, new("admin.cameras.read", ModuleId: "monitoring")));
        Assert.True(evaluator.IsAllowed(subject, new("biometrics.manage", ModuleId: "biometrics")));
        Assert.False(evaluator.IsAllowed(subject, new("biometrics.manage", "other", "garage-01")));
        Assert.False(evaluator.IsAllowed(subject, new("admin.users.manage", ModuleId: "administration")));
        Assert.False(evaluator.IsAllowed(subject, new("future.permission", "garage", "garage-01")));
    }

    [Fact]
    public void Unavailable_and_owner_only_permissions_are_never_delegated()
    {
        var subject = Subject(true, ["User"],
        [
            new("live.audio.listen", Set(PermissionScope.Global)),
            new("system.owner", Set(PermissionScope.Global))
        ]);

        Assert.False(evaluator.IsAllowed(subject, new("live.audio.listen", "garage", "garage-01")));
        Assert.False(evaluator.IsAllowed(subject, new("system.owner")));
    }

    [Fact]
    public void Legacy_viewer_role_alone_does_not_grant_resource_permission()
    {
        Assert.False(evaluator.IsAllowed(Subject(true, ["Viewer"]),
            new("live.video.view", "garage", "garage-01")));
    }

    private static AuthorizationSubject Subject(bool active, IEnumerable<string> roles,
        IReadOnlyCollection<PermissionGrant>? grants = null, bool owner = false) =>
        new(active, new HashSet<string>(roles, StringComparer.Ordinal), grants ?? [], owner);

    private static IReadOnlySet<PermissionScope> Set(params PermissionScope[] values) => new HashSet<PermissionScope>(values);
}
