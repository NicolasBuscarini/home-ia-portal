using GarageWatch.Api.Behaviors;
using GarageWatch.Api.Correlation;
using GarageWatch.Api.Endpoints;
using GarageWatch.Api.Errors;
using GarageWatch.Api.Live;
using GarageWatch.Api.Observability;
using GarageWatch.Api.Presence;
using GarageWatch.Api.Security;
using GarageWatch.Infrastructure;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Application.Profiles;
using GarageWatch.Application.PlatformEvents;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Services.AddScoped<CorrelationContext>();
builder.Services.AddScoped<ICorrelationContext>(services => services.GetRequiredService<CorrelationContext>());
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IPlatformEventOriginAccessor, HttpPlatformEventOriginAccessor>();
builder.Services.AddTransient<CorrelationPropagationHandler>();
var platformSettings = PlatformSettingsFile.LoadOrCreate(builder.Configuration);
builder.Configuration.AddInMemoryCollection(PlatformSettingsFile.ToConfiguration(platformSettings.Values));
builder.Services.AddPlatformInfrastructure(builder.Configuration);
builder.Services.AddHttpClient("monitoring-module")
    .AddHttpMessageHandler<CorrelationPropagationHandler>();
builder.Services.AddSingleton<LivePlaylistSequence>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<ClientPresenceRegistry>();
const string defaultDataProtectionPath = "/data/identity/data-protection";
builder.Services.AddDataProtection().SetApplicationName("garagewatch");
builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(provider =>
    new ConfigureNamedOptions<KeyManagementOptions>(Options.DefaultName, options =>
    {
        var path = provider.GetRequiredService<IConfiguration>()["IDENTITY_DATA_PROTECTION_PATH"]
            ?? defaultDataProtectionPath;
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("IDENTITY_DATA_PROTECTION_PATH deve ser um caminho absoluto.");
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(path), NullLoggerFactory.Instance);
    }));
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "garagewatch.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = SessionPolicy.Lifetime;
    options.SlidingExpiration = false;
    options.Events.OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromSeconds(15));
builder.Services.AddAuthorization(options =>
{
    // Legacy policy name retained during route migration; resource permissions are checked against profiles.
    options.AddPolicy("Viewer", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("Administrator", policy => policy.RequireRole("Administrator"));
    options.AddPolicy("User", policy => policy.RequireRole("User"));
});
var eventIngestLimit = 120;
var rawEventIngestLimit = builder.Configuration["EVENT_INGEST_RATE_LIMIT_PER_MINUTE"];
if (!string.IsNullOrWhiteSpace(rawEventIngestLimit) &&
    (!int.TryParse(rawEventIngestLimit, out eventIngestLimit) || eventIngestLimit is < 1 or > 10000))
    throw new InvalidOperationException("EVENT_INGEST_RATE_LIMIT_PER_MINUTE fora do limite permitido.");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
    options.AddFixedWindowLimiter("login", limiter =>
    {
        limiter.PermitLimit = 5;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("registration", limiter =>
    {
        limiter.PermitLimit = 3;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddPolicy("access-request", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 3,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("events-ingest", context =>
    {
        var raw = context.RequestServices.GetRequiredService<IConfiguration>()["EVENT_INGEST_RATE_LIMIT_PER_MINUTE"];
        var permitLimit = 120;
        if (!string.IsNullOrWhiteSpace(raw) &&
            (!int.TryParse(raw, out permitLimit) || permitLimit is < 1 or > 10000))
            throw new InvalidOperationException("EVENT_INGEST_RATE_LIMIT_PER_MINUTE fora do limite permitido.");
        return RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});
builder.Services.AddPlatformObservability(builder.Configuration, builder.Environment);
OpenApiConfiguration.AddDocumentation(builder.Services, builder.Environment);
builder.Services.AddMediatR(configuration =>
{
    configuration.RegisterServicesFromAssembly(typeof(GetSystemProfileQuery).Assembly);
    configuration.AddOpenBehavior(typeof(CorrelationLoggingBehavior<,>));
    configuration.AddOpenBehavior(typeof(ExceptionLoggingBehavior<,>));
});

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<IamDatabaseInitializer>().InitializeAsync();
app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<SafeExceptionMiddleware>();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseRateLimiter();
app.UseDocumentation();
app.UseAuthorization();
app.MapPlatformEndpoints();

app.Run();

public partial class Program { }
