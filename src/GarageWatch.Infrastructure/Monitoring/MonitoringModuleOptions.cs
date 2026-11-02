using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Monitoring;

public sealed record MonitoringModuleOptions(string Mode, string? BaseUrl)
{
    public const string ModeKey = "MONITORING_EXECUTION_MODE";
    public const string BaseUrlKey = "MONITORING_BASE_URL";
    public const string InProcess = "in-process";
    public const string Remote = "remote";

    public static MonitoringModuleOptions FromConfiguration(IConfiguration configuration)
    {
        var mode = (configuration[ModeKey] ?? InProcess).Trim().ToLowerInvariant();
        if (mode is not (InProcess or Remote))
            throw new InvalidOperationException("MONITORING_EXECUTION_MODE deve ser in-process ou remote.");
        var baseUrl = configuration[BaseUrlKey]?.Trim();
        if (mode == Remote)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("MONITORING_BASE_URL deve ser uma URI HTTP(S) interna válida no modo remote.");
        }
        return new MonitoringModuleOptions(mode, string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/'));
    }
}
