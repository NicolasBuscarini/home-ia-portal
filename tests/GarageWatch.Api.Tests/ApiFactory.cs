using GarageWatch.Application.Profiles;
using GarageWatch.Domain;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Application.Cameras;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Api.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly ISystemProfileSource? _profileSource;
    private readonly ILoggerProvider? _loggerProvider;
    private readonly ISegmentCaptureAdapter? _captureAdapter;
    private readonly string _databasePath;
    private readonly string _dataProtectionPath;
    private readonly string _captureSpoolPath;
    private readonly string? _bootstrapUsername;
    private readonly string? _bootstrapPassword;
    private readonly string _environment;
    private readonly IPublicIdGenerator? _publicIdGenerator;
    private readonly bool _useCameraCapture;
    private readonly ICameraConnectionManager? _cameraConnectionManager;
    private readonly string _monitoringMode;
    private readonly string _monitoringStatusSource;
    private readonly string _monitoringClipsSource;
    private readonly string _monitoringEventsSource;
    private readonly string _monitoringAnalysisSource;
    private readonly string _monitoringRosterSource;
    private readonly string _monitoringRetentionSource;
    private readonly string _monitoringBiometricsSource;
    private readonly GarageWatch.Application.Monitoring.IMonitoringCaptureClient? _monitoringCaptureClient;
    private readonly GarageWatch.Application.Monitoring.IMonitoringClipsClient? _monitoringClipsClient;
    private readonly GarageWatch.Application.Monitoring.IMonitoringEventsClient? _monitoringEventsClient;
    private readonly GarageWatch.Application.Monitoring.IMonitoringAnalysisClient? _monitoringAnalysisClient;
    private readonly GarageWatch.Application.Monitoring.IMonitoringRosterClient? _monitoringRosterClient;
    private readonly GarageWatch.Application.Monitoring.IMonitoringRetentionClient? _monitoringRetentionClient;
    private readonly GarageWatch.Application.Monitoring.IMonitoringBiometricClient? _monitoringBiometricClient;
    private readonly GarageWatch.Application.Portal.IActivityWatchModule? _activityWatchModule;
    private readonly string _activityWatchBaseUrl;

    public ApiFactory(ISystemProfileSource? profileSource = null, ILoggerProvider? loggerProvider = null,
        ISegmentCaptureAdapter? captureAdapter = null, string? databasePath = null,
        string? bootstrapUsername = "contract-user", string? bootstrapPassword = "Contract-Password-123!",
        string environment = "Testing", IPublicIdGenerator? publicIdGenerator = null,
        bool useCameraCapture = false, ICameraConnectionManager? cameraConnectionManager = null,
        string? dataProtectionPath = null, string monitoringMode = "in-process",
        string monitoringStatusSource = "local",
        GarageWatch.Application.Monitoring.IMonitoringCaptureClient? monitoringCaptureClient = null,
        string monitoringClipsSource = "local",
        GarageWatch.Application.Monitoring.IMonitoringClipsClient? monitoringClipsClient = null,
        string monitoringEventsSource = "local",
        GarageWatch.Application.Monitoring.IMonitoringEventsClient? monitoringEventsClient = null,
        string monitoringAnalysisSource = "local",
        GarageWatch.Application.Monitoring.IMonitoringAnalysisClient? monitoringAnalysisClient = null,
        string monitoringRosterSource = "local",
        GarageWatch.Application.Monitoring.IMonitoringRosterClient? monitoringRosterClient = null,
        string monitoringRetentionSource = "local",
        GarageWatch.Application.Monitoring.IMonitoringRetentionClient? monitoringRetentionClient = null,
        string monitoringBiometricsSource = "local",
        GarageWatch.Application.Monitoring.IMonitoringBiometricClient? monitoringBiometricClient = null,
        GarageWatch.Application.Portal.IActivityWatchModule? activityWatchModule = null,
        string activityWatchBaseUrl = "http://activitywatch.invalid")
    {
        _profileSource = profileSource;
        _loggerProvider = loggerProvider;
        _captureAdapter = captureAdapter;
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "garagewatch-tests"));
        _databasePath = databasePath ?? Path.Combine(Path.GetTempPath(), "garagewatch-tests", $"{Guid.NewGuid():N}.db");
        _dataProtectionPath = dataProtectionPath ??
            Path.Combine(Path.GetTempPath(), "garagewatch-tests", $"keys-{Guid.NewGuid():N}");
        _captureSpoolPath = Path.Combine(Path.GetTempPath(), "garagewatch-tests", $"capture-{Guid.NewGuid():N}");
        _bootstrapUsername = bootstrapUsername;
        _bootstrapPassword = bootstrapPassword;
        _environment = environment;
        _publicIdGenerator = publicIdGenerator;
        _useCameraCapture = useCameraCapture;
        _cameraConnectionManager = cameraConnectionManager;
        _monitoringMode = monitoringMode;
        _monitoringStatusSource = monitoringStatusSource;
        _monitoringClipsSource = monitoringClipsSource;
        _monitoringEventsSource = monitoringEventsSource;
        _monitoringAnalysisSource = monitoringAnalysisSource;
        _monitoringRosterSource = monitoringRosterSource;
        _monitoringRetentionSource = monitoringRetentionSource;
        _monitoringBiometricsSource = monitoringBiometricsSource;
        _monitoringCaptureClient = monitoringCaptureClient;
        _monitoringClipsClient = monitoringClipsClient;
        _monitoringEventsClient = monitoringEventsClient;
        _monitoringAnalysisClient = monitoringAnalysisClient;
        _monitoringRosterClient = monitoringRosterClient;
        _monitoringRetentionClient = monitoringRetentionClient;
        _monitoringBiometricClient = monitoringBiometricClient;
        _activityWatchModule = activityWatchModule;
        _activityWatchBaseUrl = activityWatchBaseUrl;
    }

    public string DatabasePath => _databasePath;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseWebRoot(Environment.GetEnvironmentVariable("GARAGEWATCH_WEB_ROOT") ?? "/app/web");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AUTH_USERNAME"] = _bootstrapUsername,
            ["AUTH_PASSWORD"] = _bootstrapPassword,
            ["AUTH_EMAIL"] = "contract-admin@example.invalid",
            ["IDENTITY_DB_PATH"] = _databasePath,
            ["IDENTITY_DATA_PROTECTION_PATH"] = _dataProtectionPath,
            ["CAPTURE_SPOOL_DIRECTORY"] = _captureSpoolPath,
            ["MONITORING_EXECUTION_MODE"] = _monitoringMode,
            ["MONITORING_STATUS_SOURCE"] = _monitoringStatusSource,
            ["MONITORING_CLIPS_SOURCE"] = _monitoringClipsSource,
            ["MONITORING_EVENTS_SOURCE"] = _monitoringEventsSource,
            ["MONITORING_ANALYSIS_SOURCE"] = _monitoringAnalysisSource,
            ["MONITORING_ROSTER_SOURCE"] = _monitoringRosterSource,
            ["MONITORING_RETENTION_SOURCE"] = _monitoringRetentionSource,
            ["MONITORING_BIOMETRICS_SOURCE"] = _monitoringBiometricsSource,
            ["MONITORING_BASE_URL"] = "http://monitoring.invalid",
            ["MONITORING_SERVICE_TOKEN_SECRET"] = "test-monitoring-service-secret-0123456789abcdef",
            ["ACTIVITYWATCH_BASE_URL"] = _activityWatchBaseUrl
        }));
        if (_loggerProvider is not null)
        {
            builder.ConfigureLogging(logging => logging.AddProvider(_loggerProvider));
        }

        builder.ConfigureTestServices(services =>
        {
            if (_publicIdGenerator is not null)
            {
                services.RemoveAll<IPublicIdGenerator>();
                services.AddSingleton<IPublicIdGenerator>(_publicIdGenerator);
            }
            services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
            var profileSource = _profileSource;
            if (profileSource is not null)
            {
                services.RemoveAll<ISystemProfileSource>();
                services.AddSingleton<ISystemProfileSource>(profileSource);
            }
            if (_captureAdapter is not null)
            {
                services.RemoveAll<ISegmentCaptureAdapter>();
                services.AddSingleton(_captureAdapter);
            }
            if (_useCameraCapture)
            {
                services.RemoveAll<CaptureOptions>();
                services.AddSingleton(new CaptureOptions("ffmpeg-rtsp", "garage-01", _captureSpoolPath,
                    1, 4, 1_048_576, "rtsp://neolink:8554/garage/mainStream", "ffmpeg", "ffprobe", true));
            }
            if (_cameraConnectionManager is not null)
            {
                services.RemoveAll<ICameraConnectionManager>();
                services.AddSingleton(_cameraConnectionManager);
            }
            if (_monitoringCaptureClient is not null)
            {
                services.RemoveAll<GarageWatch.Application.Monitoring.IMonitoringCaptureClient>();
                services.AddSingleton(_monitoringCaptureClient);
            }
            if (_monitoringClipsClient is not null)
            {
                services.RemoveAll<GarageWatch.Application.Monitoring.IMonitoringClipsClient>();
                services.AddSingleton(_monitoringClipsClient);
            }
            if (_monitoringEventsClient is not null)
            {
                services.RemoveAll<GarageWatch.Application.Monitoring.IMonitoringEventsClient>();
                services.AddSingleton(_monitoringEventsClient);
            }
            if (_monitoringAnalysisClient is not null)
            {
                services.RemoveAll<GarageWatch.Application.Monitoring.IMonitoringAnalysisClient>();
                services.AddSingleton(_monitoringAnalysisClient);
            }
            if (_monitoringRosterClient is not null)
            {
                services.RemoveAll<GarageWatch.Application.Monitoring.IMonitoringRosterClient>();
                services.AddSingleton(_monitoringRosterClient);
            }
            if (_monitoringRetentionClient is not null)
            {
                services.RemoveAll<GarageWatch.Application.Monitoring.IMonitoringRetentionClient>();
                services.AddSingleton(_monitoringRetentionClient);
            }
            if (_monitoringBiometricClient is not null)
            {
                services.RemoveAll<GarageWatch.Application.Monitoring.IMonitoringBiometricClient>();
                services.AddSingleton(_monitoringBiometricClient);
            }
            if (_activityWatchModule is not null)
            {
                services.RemoveAll<GarageWatch.Application.Portal.IActivityWatchModule>();
                services.AddSingleton(_activityWatchModule);
            }
        });
    }
}

public sealed class UnavailableCaptureAdapter : ISegmentCaptureAdapter
{
    public string Name => "contract-unavailable-source";
    public string Version => "1.0";
    public IReadOnlyList<string> Capabilities => ["contract-test"];

    public async IAsyncEnumerable<CapturedSegment> CaptureAsync(string workDirectory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (cancellationToken.IsCancellationRequested) yield break;
        throw new InvalidOperationException("contract_source_unavailable");
    }
}

public sealed class ThrowingSystemProfileSource : ISystemProfileSource
{
    public SystemProfile Get() => throw new InvalidOperationException("secret-value-must-not-leak");
}
