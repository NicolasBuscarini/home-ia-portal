using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Integrations;

/// <summary>
/// Read-only ActivityWatch integration settings. The optional token is a scope-minimal
/// credential injected by environment and is never returned, logged or bundled.
/// </summary>
public sealed record ActivityWatchOptions(string BaseUrl, string? ApiToken, int TimeoutSeconds, int LookbackHours)
{
    public const string HttpClientName = "activitywatch";
    public const string BaseUrlKey = "ACTIVITYWATCH_BASE_URL";
    public const string ApiTokenKey = "ACTIVITYWATCH_API_TOKEN";
    public const string TimeoutSecondsKey = "ACTIVITYWATCH_TIMEOUT_SECONDS";
    public const string LookbackHoursKey = "ACTIVITYWATCH_LOOKBACK_HOURS";
    public const string DefaultBaseUrl = "http://127.0.0.1:5600";
    public const int DefaultTimeoutSeconds = 3;
    public const int DefaultLookbackHours = 24;


    public static ActivityWatchOptions FromConfiguration(IConfiguration configuration)
    {
        var rawBaseUrl = configuration[BaseUrlKey];
        var baseUrl = string.IsNullOrWhiteSpace(rawBaseUrl) ? DefaultBaseUrl : rawBaseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath is not ("" or "/"))
            throw new InvalidOperationException(
                $"{BaseUrlKey} deve ser uma URI absoluta http(s) sem credenciais, caminho, consulta ou fragmento.");

        var rawToken = configuration[ApiTokenKey];
        string? token = null;
        if (!string.IsNullOrWhiteSpace(rawToken))
        {
            token = rawToken.Trim();
            if (token.Length > 512 || token.Any(char.IsControl))
                throw new InvalidOperationException($"{ApiTokenKey} inválido.");
        }

        var timeout = ParseBounded(configuration, TimeoutSecondsKey, DefaultTimeoutSeconds, 1, 30);
        var lookback = ParseBounded(configuration, LookbackHoursKey, DefaultLookbackHours, 1, 168);
        return new ActivityWatchOptions(baseUrl, token, timeout, lookback);
    }

    private static int ParseBounded(IConfiguration configuration, string key, int fallback, int minimum, int maximum)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw.Trim(), out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{key} deve ser um inteiro entre {minimum} e {maximum}.");
        return value;
    }
}
