namespace GarageWatch.Api.Security;

public static class SessionPolicy
{
    /// <summary>Fixed lifetime from login; SecurityStamp renewal keeps the original expiry.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
}

public sealed record LoginRequest(string Username, string Password);
public sealed record AuthenticatedUser(string Username, string Role, bool CanMonitor = false);
public sealed record RegisterRequest(string? Username, string? Password, string? ConfirmPassword);
