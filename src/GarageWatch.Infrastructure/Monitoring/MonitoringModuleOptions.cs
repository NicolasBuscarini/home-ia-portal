using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Monitoring;

/// <summary>
/// Configuração do serviço de Monitoramento. O portal não mantém caminho embutido desde
/// T16-REMOVE-EMBEDDED: status, câmeras, clipes, eventos, análise, roster, retenção,
/// biometria e mídia de clipes são sempre lidos do serviço por contrato HTTP.
/// Chaves antigas de seleção (`MONITORING_*_SOURCE`, `MONITORING_EXECUTION_MODE`) são
/// ignoradas de propósito para não falhar o startup durante a migração de configuração.
/// </summary>
public sealed record MonitoringModuleOptions(string BaseUrl)
{
    public const string BaseUrlKey = "MONITORING_BASE_URL";

    public static MonitoringModuleOptions FromConfiguration(IConfiguration configuration)
    {
        var baseUrl = configuration[BaseUrlKey]?.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("MONITORING_BASE_URL deve ser uma URI HTTP(S) interna válida.");
        return new MonitoringModuleOptions(baseUrl!.TrimEnd('/'));
    }
}
