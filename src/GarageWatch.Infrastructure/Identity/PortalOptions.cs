using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Identity;

public sealed record PortalOptions(string ResidenceId)
{
    public const string ResidenceIdKey = "PORTAL_RESIDENCE_ID";
    public const string DefaultResidenceId = "home";

    public static PortalOptions FromConfiguration(IConfiguration configuration)
    {
        var value = (configuration[ResidenceIdKey] ?? DefaultResidenceId).Trim();
        if (value.Length is < 1 or > 64 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidOperationException("PORTAL_RESIDENCE_ID deve ser um identificador local válido.");
        return new PortalOptions(value);
    }
}
