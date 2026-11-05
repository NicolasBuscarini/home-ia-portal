using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace GarageWatch.Api.Correlation;

public static class PlatformEventSession
{
    /// <summary>
    /// Login issues an opaque session claim. Cookies issued before that claim existed fall back to
    /// a one-way fingerprint of the authentication cookie so old sessions remain attributable
    /// without ever storing the cookie or its secret.
    /// </summary>
    public static string? Resolve(HttpContext context)
    {
        var claim = context.User.FindFirst("session_id")?.Value;
        if (!string.IsNullOrEmpty(claim)) return claim;
        var cookie = context.Request.Cookies["garagewatch.session"];
        if (string.IsNullOrEmpty(cookie)) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cookie)))[..32].ToLowerInvariant();
    }
}
