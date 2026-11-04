// ROLLBACK-ONLY (ADR109): o caminho `in-process`/`local` e a implementação embutida de
// Monitoramento existem apenas para rollback de configuração sobre o mesmo spool.
// Não adicionar funcionalidade nova aqui; a remoção está planejada em T16-REMOVE-EMBEDDED.
using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Monitoring;

public sealed record MonitoringModuleOptions(string Mode, string? BaseUrl, string StatusSource,
    string ClipsSource = "local", string EventsSource = "local", string AnalysisSource = "local",
    string RosterSource = "local", string RetentionSource = "local", string BiometricsSource = "local",
    string LiveSource = "local", string ClipsWriteSource = "local")
{
    public const string ModeKey = "MONITORING_EXECUTION_MODE";
    public const string BaseUrlKey = "MONITORING_BASE_URL";
    public const string StatusSourceKey = "MONITORING_STATUS_SOURCE";
    public const string ClipsSourceKey = "MONITORING_CLIPS_SOURCE";
    public const string EventsSourceKey = "MONITORING_EVENTS_SOURCE";
    public const string AnalysisSourceKey = "MONITORING_ANALYSIS_SOURCE";
    public const string RosterSourceKey = "MONITORING_ROSTER_SOURCE";
    public const string RetentionSourceKey = "MONITORING_RETENTION_SOURCE";
    public const string BiometricsSourceKey = "MONITORING_BIOMETRICS_SOURCE";
    public const string LiveSourceKey = "MONITORING_LIVE_SOURCE";
    public const string ClipsWriteSourceKey = "MONITORING_CLIPS_WRITE_SOURCE";
    public const string InProcess = "in-process";
    public const string Remote = "remote";
    public const string LocalStatus = "local";

    public bool UsesRemote =>
        Mode == Remote || StatusSource == Remote || ClipsSource == Remote || EventsSource == Remote ||
        AnalysisSource == Remote || RosterSource == Remote || RetentionSource == Remote ||
        BiometricsSource == Remote || LiveSource == Remote || ClipsWriteSource == Remote;

    public static MonitoringModuleOptions FromConfiguration(IConfiguration configuration)
    {
        var mode = (configuration[ModeKey] ?? InProcess).Trim().ToLowerInvariant();
        if (mode is not (InProcess or Remote))
            throw new InvalidOperationException("MONITORING_EXECUTION_MODE deve ser in-process ou remote.");
        var statusSource = (configuration[StatusSourceKey] ?? LocalStatus).Trim().ToLowerInvariant();
        if (statusSource is not (LocalStatus or Remote))
            throw new InvalidOperationException("MONITORING_STATUS_SOURCE deve ser local ou remote.");
        var clipsSource = (configuration[ClipsSourceKey] ?? LocalStatus).Trim().ToLowerInvariant();
        if (clipsSource is not (LocalStatus or Remote))
            throw new InvalidOperationException("MONITORING_CLIPS_SOURCE deve ser local ou remote.");
        var eventsSource = (configuration[EventsSourceKey] ?? LocalStatus).Trim().ToLowerInvariant();
        if (eventsSource is not (LocalStatus or Remote))
            throw new InvalidOperationException("MONITORING_EVENTS_SOURCE deve ser local ou remote.");
        var analysisSource = (configuration[AnalysisSourceKey] ?? LocalStatus).Trim().ToLowerInvariant();
        if (analysisSource is not (LocalStatus or Remote))
            throw new InvalidOperationException("MONITORING_ANALYSIS_SOURCE deve ser local ou remote.");
        var rosterSource = (configuration[RosterSourceKey] ?? LocalStatus).Trim().ToLowerInvariant();
        if (rosterSource is not (LocalStatus or Remote))
            throw new InvalidOperationException("MONITORING_ROSTER_SOURCE deve ser local ou remote.");
        var retentionSource = (configuration[RetentionSourceKey] ?? LocalStatus).Trim().ToLowerInvariant();
        if (retentionSource is not (LocalStatus or Remote))
            throw new InvalidOperationException("MONITORING_RETENTION_SOURCE deve ser local ou remote.");
        var biometricsSource = (configuration[BiometricsSourceKey] ?? LocalStatus).Trim().ToLowerInvariant();
        if (biometricsSource is not (LocalStatus or Remote))
            throw new InvalidOperationException("MONITORING_BIOMETRICS_SOURCE deve ser local ou remote.");
        var liveSource = (configuration[LiveSourceKey] ?? LocalStatus).Trim().ToLowerInvariant();
        if (liveSource is not (LocalStatus or Remote))
            throw new InvalidOperationException("MONITORING_LIVE_SOURCE deve ser local ou remote.");
        var clipsWriteSource = (configuration[ClipsWriteSourceKey] ?? LocalStatus).Trim().ToLowerInvariant();
        if (clipsWriteSource is not (LocalStatus or Remote))
            throw new InvalidOperationException("MONITORING_CLIPS_WRITE_SOURCE deve ser local ou remote.");
        var baseUrl = configuration[BaseUrlKey]?.Trim();
        if (mode == Remote || statusSource == Remote || clipsSource == Remote || eventsSource == Remote ||
            analysisSource == Remote || rosterSource == Remote || retentionSource == Remote ||
            biometricsSource == Remote || liveSource == Remote || clipsWriteSource == Remote)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("MONITORING_BASE_URL deve ser uma URI HTTP(S) interna válida no modo remote.");
        }
        return new MonitoringModuleOptions(mode, string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/'),
            statusSource, clipsSource, eventsSource, analysisSource, rosterSource, retentionSource,
            biometricsSource, liveSource, clipsWriteSource);
    }
}
