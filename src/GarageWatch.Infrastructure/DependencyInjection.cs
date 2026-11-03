using GarageWatch.Application.Profiles;
using GarageWatch.Application.Identity;
using GarageWatch.Application.Cameras;
using GarageWatch.Application.Authorization;
using GarageWatch.Application.Portal;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Monitoring;
using GarageWatch.Application.Biometrics;
using GarageWatch.Infrastructure.Biometrics;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Identity.Repositories;
using GarageWatch.Infrastructure.Analysis;
using GarageWatch.Application.Analysis;
using GarageWatch.Infrastructure.Events;
using GarageWatch.Application.Events;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GarageWatch.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPlatformInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ISystemProfileSource, EnvironmentSystemProfileSource>();
        services.AddSingleton<IPublicIdGenerator, PublicIdGenerator>();
        services.AddSingleton<IBiometricDataProtector, Biometrics.AesGcmBiometricDataProtector>();
        services.AddSingleton<Biometrics.BiometricRetentionWorker>();
        services.AddDbContext<IamDbContext>((provider, options) =>
        {
            var settings = provider.GetRequiredService<IConfiguration>();
            var databasePath = settings[IdentityBootstrapOptions.DatabasePathKey] ?? IdentityBootstrapOptions.DefaultDatabasePath;
            if (!Path.IsPathFullyQualified(databasePath))
                throw new InvalidOperationException("IDENTITY_DB_PATH deve ser um caminho absoluto.");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 5,
                Pooling = true
            }.ToString();
            options.UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly(typeof(IamDbContext).Assembly.FullName));
        });
        services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.User.RequireUniqueEmail = false;
            options.Password.RequiredLength = 6;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<IamDbContext>()
            .AddSignInManager();
        services.AddScoped<IamDatabaseInitializer>();
        services.AddScoped<IdentityRegistrationService>();
        services.AddScoped<IdentityAdministrationService>();
        services.AddScoped<AccessProfileService>();
        services.AddScoped<PortalService>();
        services.AddScoped<IPortalModuleCatalog>(provider => provider.GetRequiredService<PortalService>());
        services.AddScoped<IPortalNavigation>(provider => provider.GetRequiredService<PortalService>());
        services.AddScoped<IPortalPreferences>(provider => provider.GetRequiredService<PortalService>());
        services.AddScoped<IPortalAuthorizationDecisions>(provider => provider.GetRequiredService<PortalService>());
        services.AddScoped<AccessRequestService>();
        services.AddScoped<ClipAnalysisDispatchService>();
        services.AddScoped<CameraClipTriggerConfigurationService>();
        services.AddScoped<KnownPersonService>();
        services.AddScoped<RemoteKnownPeopleGateway>();
        services.AddScoped<IKnownPeopleGateway>(provider =>
            provider.GetRequiredService<MonitoringModuleOptions>().RosterSource == MonitoringModuleOptions.Remote
                ? provider.GetRequiredService<RemoteKnownPeopleGateway>()
                : provider.GetRequiredService<KnownPersonService>());
        services.AddScoped<FaceExtractorRegistryService>();
        services.AddScoped<BiometricEnrollmentService>();
        services.AddScoped<LocalBiometricEnrollmentGateway>();
        services.AddScoped<RemoteBiometricEnrollmentGateway>();
        services.AddScoped<IBiometricEnrollmentGateway>(provider =>
            provider.GetRequiredService<MonitoringModuleOptions>().BiometricsSource == MonitoringModuleOptions.Remote
                ? provider.GetRequiredService<RemoteBiometricEnrollmentGateway>()
                : provider.GetRequiredService<LocalBiometricEnrollmentGateway>());
        services.AddScoped<ClipRetentionService>();
        services.AddScoped<LocalClipRetentionGateway>();
        services.AddScoped<RemoteClipRetentionGateway>();
        services.AddScoped<IClipRetentionGateway>(provider =>
            provider.GetRequiredService<MonitoringModuleOptions>().RetentionSource == MonitoringModuleOptions.Remote
                ? provider.GetRequiredService<RemoteClipRetentionGateway>()
                : provider.GetRequiredService<LocalClipRetentionGateway>());
        services.AddSingleton<ClipReadLeaseRegistry>();
        services.AddScoped<ICameraCatalog, CameraCatalogService>();
        services.AddScoped<ICameraResourceAccess, CameraResourceAccessService>();
        services.AddHttpClient("neolink-control", client =>
        {
            var address = configuration["NEOLINK_CONTROL_URL"] ?? "http://neolink:8555";
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
                throw new InvalidOperationException("NEOLINK_CONTROL_URL deve ser uma URI HTTP interna válida.");
            client.BaseAddress = uri;
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddScoped<ICameraConnectionManager, NeolinkCameraConnectionManager>();
        services.AddSingleton<IPermissionEvaluator, PermissionEvaluator>();
        services.AddSingleton<IamTransactionGate>();
        services.AddScoped<IIamUnitOfWork, IamUnitOfWork>();
        services.AddScoped<ICameraAccessGrantRepository, CameraAccessGrantRepository>();
        services.AddScoped<IIamAuditEventRepository, IamAuditEventRepository>();
        services.AddScoped(provider => IdentityBootstrapOptions.FromConfiguration(
            provider.GetRequiredService<IConfiguration>()));
        var captureOptions = CaptureOptions.FromEnvironment(configuration);
        var triggerOptions = ClipTriggerOptions.FromEnvironment(configuration);
        var clipAnalysisOptions = ClipAnalysisDispatchOptions.FromConfiguration(configuration);
        var detectorOptions = OnnxDetectorOptions.FromConfiguration(configuration);
        var eventOptions = EventEngineOptions.FromConfiguration(configuration, captureOptions);
        var clipArchiveOptions = ClipArchiveOptions.FromConfiguration(configuration, captureOptions);
        var clipPlaybackOptions = ClipPlaybackOptions.FromEnvironment(configuration, captureOptions);
        var faceExtractorOptions = FaceExtractorOptions.FromEnvironment(configuration);
        var portalOptions = PortalOptions.FromConfiguration(configuration);
        var monitoringOptions = MonitoringModuleOptions.FromConfiguration(configuration);
        var monitoringTokenOptions = MonitoringServiceTokenOptions.FromConfiguration(configuration);
        if (monitoringOptions.UsesRemote && !monitoringTokenOptions.Configured)
            throw new InvalidOperationException(
                "MONITORING_SERVICE_TOKEN_SECRET deve ter ao menos 32 bytes quando qualquer fonte do Monitoramento é remote.");
        services.AddSingleton(monitoringTokenOptions);
        services.AddSingleton(provider => new MonitoringServiceTokenIssuer(
            provider.GetRequiredService<MonitoringServiceTokenOptions>(), TimeProvider.System));
        services.AddTransient<MonitoringServiceTokenHandler>();
        if (!captureOptions.LocalCaptureEnabled &&
            monitoringOptions.StatusSource != MonitoringModuleOptions.Remote)
            throw new InvalidOperationException(
                "CAPTURE_AUTHORITY=remote exige MONITORING_STATUS_SOURCE=remote para evitar status local enganoso.");
        services.AddSingleton(provider => MonitoringModuleOptions.FromConfiguration(
            provider.GetRequiredService<IConfiguration>()));
        services.AddHttpClient("monitoring-module", client =>
        {
            if (monitoringOptions.Mode == MonitoringModuleOptions.Remote ||
                monitoringOptions.ClipsSource == MonitoringModuleOptions.Remote ||
                monitoringOptions.EventsSource == MonitoringModuleOptions.Remote ||
                monitoringOptions.AnalysisSource == MonitoringModuleOptions.Remote ||
                monitoringOptions.RosterSource == MonitoringModuleOptions.Remote ||
                monitoringOptions.RetentionSource == MonitoringModuleOptions.Remote ||
                monitoringOptions.BiometricsSource == MonitoringModuleOptions.Remote ||
                monitoringOptions.LiveSource == MonitoringModuleOptions.Remote ||
                monitoringOptions.ClipsWriteSource == MonitoringModuleOptions.Remote)
                client.BaseAddress = new Uri(monitoringOptions.BaseUrl! + "/", UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(3);
        }).AddHttpMessageHandler<MonitoringServiceTokenHandler>();
        services.AddScoped<IMonitoringModuleClient, MonitoringModuleClient>();
        services.AddScoped<IMonitoringCaptureClient, MonitoringCaptureClient>();
        services.AddScoped<IMonitoringClipsClient, MonitoringClipsClient>();
        services.AddScoped<IMonitoringRosterClient, MonitoringRosterClient>();
        services.AddScoped<IMonitoringRetentionClient, MonitoringRetentionClient>();
        services.AddScoped<IMonitoringBiometricClient, MonitoringBiometricClient>();
        services.AddScoped<IMonitoringAnalysisClient, MonitoringAnalysisClient>();
        services.AddScoped<IMonitoringLiveClient, MonitoringLiveClient>();
        services.AddScoped<IMonitoringClipMediaClient, MonitoringClipMediaClient>();
        services.AddSingleton<IMonitoringEventsClient, MonitoringEventsClient>();
        services.AddSingleton<RemoteEventJournal>();
        services.AddSingleton(captureOptions);
        services.AddSingleton(portalOptions);
        services.AddSingleton(triggerOptions);
        services.AddSingleton(clipAnalysisOptions);
        services.AddSingleton(detectorOptions);
        services.AddSingleton(eventOptions);
        services.AddSingleton(clipArchiveOptions);
        services.AddSingleton(clipPlaybackOptions);
        services.AddSingleton(faceExtractorOptions);
        services.AddSingleton<FaceExtractorRuntime>();
        services.AddSingleton<IFaceEnrollmentExtractor, OnnxFaceEnrollmentExtractor>();
        services.AddSingleton<VersionedEventRuleEngine>();
        services.AddSingleton<LocalEventJournal>();
        services.AddSingleton<IEventJournal>(provider =>
            provider.GetRequiredService<MonitoringModuleOptions>().EventsSource == MonitoringModuleOptions.Remote
                ? provider.GetRequiredService<RemoteEventJournal>()
                : provider.GetRequiredService<LocalEventJournal>());
        services.AddSingleton<LocalSegmentSpool>();
        services.AddSingleton<ISegmentCaptureAdapter>(captureOptions.Profile == "synthetic"
            ? new SyntheticSegmentCaptureAdapter(captureOptions)
            : new FfmpegRtspCaptureAdapter(captureOptions));
        services.AddSingleton<IVisualChangeDetector, FfmpegVisualChangeDetector>();
        services.AddSingleton<IObjectDetector, OnnxObjectDetector>();
        services.AddSingleton<IObjectTracker, GreedyIouObjectTracker>();
        services.AddSingleton<DetectionProcessor>();
        services.AddSingleton<ObjectDetectionWorker>();
        services.AddSingleton<IClipMediaAssembler>(provider =>
            provider.GetRequiredService<MonitoringModuleOptions>().ClipsWriteSource ==
            MonitoringModuleOptions.Remote
                ? ActivatorUtilities.CreateInstance<RemoteClipMediaAssembler>(provider)
                : ActivatorUtilities.CreateInstance<LocalClipMediaAssembler>(provider));
        services.AddSingleton<ClipPlaybackCache>();
        services.AddSingleton<IClipArchiveAdapter, LocalFilesystemClipArchiveAdapter>();
        services.AddSingleton<ClipArchiveWorker>();
        services.AddSingleton<ClipRetentionWorker>();
        services.AddSingleton<IClipArchiveCapacityManager>(provider => provider.GetRequiredService<ClipRetentionWorker>());
        services.AddScoped<ClipLibraryService>();
        services.AddSingleton<IClipAnalysisAutoDispatcher, ClipAnalysisAutoDispatcher>();
        services.AddSingleton<ClipTriggerWorker>();
        services.AddSingleton<CaptureWorker>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ClipTriggerWorker>());
        if (monitoringOptions.RetentionSource != MonitoringModuleOptions.Remote)
        {
            services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ClipArchiveWorker>());
            services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ClipRetentionWorker>());
        }
        if (monitoringOptions.BiometricsSource != MonitoringModuleOptions.Remote)
            services.AddSingleton<IHostedService>(provider =>
                provider.GetRequiredService<Biometrics.BiometricRetentionWorker>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ObjectDetectionWorker>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<CaptureWorker>());
        return services;
    }
}
