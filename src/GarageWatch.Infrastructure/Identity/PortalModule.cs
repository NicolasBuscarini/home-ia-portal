namespace GarageWatch.Infrastructure.Identity;

public sealed class PortalModule
{
    public string ModuleId { get; set; } = string.Empty;
    public string DisplayNameKey { get; set; } = string.Empty;
    public string ContractVersion { get; set; } = "1.0";
    public string ApiBaseUrl { get; set; } = "/";
    public string? HealthPath { get; set; }
    public string? RequiredPermission { get; set; }
    public string MenuGroup { get; set; } = "overview";
    public int MenuOrder { get; set; }
    public string MenuIcon { get; set; } = "module";
    public string MenuLabelKey { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public List<PortalModuleMenuItem> Items { get; set; } = [];
}

public sealed class PortalModuleMenuItem
{
    public string ModuleId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string LabelKey { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string? RequiredPermission { get; set; }
    public PortalModule? Module { get; set; }
}

public sealed class PortalUserPreference
{
    public Guid UserId { get; set; }
    public string ThemeId { get; set; } = string.Empty;
    public string LanguageTag { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public ApplicationUser? User { get; set; }
}
