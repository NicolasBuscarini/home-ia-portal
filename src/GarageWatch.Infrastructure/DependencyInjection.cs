using GarageWatch.Application.PlatformEvents;
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
using GarageWatch.Infrastructure.Health;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Identity.Repositories;
using GarageWatch.Infrastructure.Integrations;
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
        services.AddSingleton<IIntegrationCredentialProtector, Integrations.AesGcmIntegrationCredentialProtector>();
        services.AddScoped<IIntegrationCredentialStore, Integrations.IntegrationCredentialStore>();
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
        services.AddScoped<IPortalHealth, PortalHealthService>();
        services.AddScoped<IPortalModuleHealthProbe, MonitoringModuleHealthProbe>();
        services.AddScoped<IPortalModuleHealthProbe, ActivityWatchHealthProbe>();
        services.AddSingleton<IPortalModuleHealthProbe>(new InProcessModuleHealthProbe("administration"));
        services.AddScoped<AccessRequestService>();
        services.AddScoped<ClipAnalysisDispatchService>();
        services.AddScoped<CameraClipTriggerConfigurationService>();
        services.AddScoped<KnownPersonService>();
        services.AddScoped<RemoteKnownPeopleGateway>();
        services.AddScoped<IKnownPeopleGateway, RemoteKnownPeopleGateway>();
        services.AddScoped<FaceExtractorRegistryService>();
        services.AddScoped<BiometricEnrollmentService>();
        services.AddScoped<IBiometricPersonLinkStore, BiometricPersonLinkStore>();
        services.AddScoped<BiometricPersonLinkService>();
        services.AddSingleton<IIdentityMatcher, CosineIdentityMatcher>();
        services.AddScoped<LocalIdentityEmbeddingSource>();
        services.AddScoped<RemoteIdentityEmbeddingSource>();
        services.AddScoped<IIdentityEmbeddingSource>(provider =>
            string.Equals(provider.GetRequiredService<IConfiguration>()["MONITORING_BIOMETRICS_SOURCE"],
                "local", StringComparison.OrdinalIgnoreCase)
                ? provider.GetRequiredService<LocalIdentityEmbeddingSource>()
                : provider.GetRequiredService<RemoteIdentityEmbeddingSource>());
        services.AddScoped<IdentityMatchService>();
        services.AddScoped<LocalBiometricEnrollmentGateway>();
        services.AddScoped<RemoteBiometricEnrollmentGateway>();
        services.AddScoped<IBiometricEnrollmentGateway, RemoteBiometricEnrollmentGateway>();
        services.AddScoped<ClipRetentionService>();
        services.AddScoped<LocalClipRetentionGateway>();
        services.AddScoped<RemoteClipRetentionGateway>();
        services.AddScoped<IClipRetentionGateway, RemoteClipRetentionGateway>();
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
        services.AddScoped<IPlatformEventRepository, PlatformEventRepository>();
        services.AddScoped<IPlatformEventWriter, PlatformEventWriter>();
        services.AddScoped<PlatformEventBackfillService>();
        services.AddScoped<PlatformEventRetentionService>();
        services.AddSingleton(provider => PlatformEventServiceTokenOptions.FromConfiguration(
            provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<PlatformEventServiceTokenValidator>();
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
        var platformEventRetentionOptions = PlatformEventRetentionOptions.FromConfiguration(configuration);
        services.AddSingleton(provider =>
        {
            var token = MonitoringServiceTokenOptions.FromConfiguration(
                provider.GetRequiredService<IConfiguration>());
            if (!token.Configured)
                throw new InvalidOperationException(
                    "MONITORING_SERVICE_TOKEN_SECRET deve ter ao menos 32 bytes: o portal sempre acessa o Monitoramento pelo serviço.");
            return token;
        });
        services.AddSingleton(provider => new MonitoringServiceTokenIssuer(
            provider.GetRequiredService<MonitoringServiceTokenOptions>(), TimeProvider.System));
        services.AddTransient<MonitoringServiceTokenHandler>();
        services.AddSingleton(provider => MonitoringModuleOptions.FromConfiguration(
            provider.GetRequiredService<IConfiguration>()));
        services.AddHttpClient("monitoring-module", (provider, client) =>
        {
            var monitoring = provider.GetRequiredService<MonitoringModuleOptions>();
            client.BaseAddress = new Uri(monitoring.BaseUrl + "/", UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(3);
        }).AddHttpMessageHandler<MonitoringServiceTokenHandler>();
        services.AddScoped<IMonitoringModuleClient, MonitoringModuleClient>();
        services.AddScoped<IMonitoringCaptureClient, MonitoringCaptureClient>();
        services.AddScoped<IMonitoringClipsClient, MonitoringClipsClient>();
        services.AddScoped<IMonitoringRosterClient, MonitoringRosterClient>();
        services.AddScoped<IMonitoringRetentionClient, MonitoringRetentionClient>();
        services.AddScoped<MonitoringBiometricClient>();
        services.AddScoped<IMonitoringBiometricClient>(provider =>
            provider.GetRequiredService<MonitoringBiometricClient>());
        services.AddScoped<IMonitoringBiometricCiphertextClient>(provider =>
            provider.GetRequiredService<MonitoringBiometricClient>());
        services.AddScoped<IMonitoringAnalysisClient, MonitoringAnalysisClient>();
        services.AddScoped<IMonitoringLiveClient, MonitoringLiveClient>();
        // O assembler remoto de clipes é singleton; o cliente não mantém estado e só usa
        // IHttpClientFactory + options, ambos singleton, então também é registrado como singleton.
        services.AddSingleton<IMonitoringClipMediaClient, MonitoringClipMediaClient>();
        services.AddSingleton<IMonitoringEventsClient, MonitoringEventsClient>();
        services.AddSingleton<RemoteEventJournal>();
        var activityWatchOptions = ActivityWatchOptions.FromConfiguration(configuration);
        services.AddSingleton(activityWatchOptions);
        services.AddHttpClient(ActivityWatchOptions.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(activityWatchOptions.BaseUrl + "/", UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(activityWatchOptions.TimeoutSeconds);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddScoped<IActivityWatchModule, ActivityWatchClient>();
        services.AddSingleton(captureOptions);
        services.AddSingleton(portalOptions);
        services.AddSingleton(platformEventRetentionOptions);
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
        services.AddSingleton<IEventJournal>(provider => provider.GetRequiredService<RemoteEventJournal>());
        services.AddSingleton<LocalSegmentSpool>();
        services.AddSingleton<IVisualChangeDetector, FfmpegVisualChangeDetector>();
        services.AddSingleton<IClipMediaAssembler>(provider =>
            ActivatorUtilities.CreateInstance<RemoteClipMediaAssembler>(provider));
        services.AddSingleton<ClipPlaybackCache>();
        services.AddScoped<ClipLibraryService>();
        services.AddSingleton<IClipAnalysisAutoDispatcher, ClipAnalysisAutoDispatcher>();
        services.AddSingleton<ClipTriggerWorker>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ClipTriggerWorker>());
        services.AddSingleton<SpoolWatcherWorker>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<SpoolWatcherWorker>());
        services.AddSingleton<IHostedService, PlatformEventRetentionWorker>();
        return services;
    }
}
