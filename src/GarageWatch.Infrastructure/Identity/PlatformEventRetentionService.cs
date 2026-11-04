using GarageWatch.Application.PlatformEvents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Identity;

public sealed class PlatformEventRetentionService(
    IamDbContext database, PlatformEventRetentionOptions options, TimeProvider timeProvider)
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Enabled) return 0;

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var startOfToday = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var cutoff = now.AddDays(-options.RetentionDays);
        if (cutoff > startOfToday) cutoff = startOfToday;
        var criticalCutoff = now.AddDays(-options.CriticalRetentionDays);

        var deleted = 0;
        while (true)
        {
            var batch = await database.PlatformEvents
                .Where(entry => entry.OccurredAtUtc < cutoff
                    && !(entry.Severity == PlatformEventSeverities.Critical
                        && entry.OccurredAtUtc >= criticalCutoff))
                .OrderBy(entry => entry.OccurredAtUtc)
                .Take(options.BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0) break;

            database.PlatformEvents.RemoveRange(batch);
            await database.SaveChangesAsync(cancellationToken);
            deleted += batch.Count;
            if (batch.Count < options.BatchSize) break;
        }

        return deleted;
    }
}

public sealed class PlatformEventRetentionWorker(
    IServiceScopeFactory scopeFactory,
    PlatformEventRetentionOptions options,
    ILogger<PlatformEventRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.SweepIntervalMinutes));
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var deleted = await scope.ServiceProvider
                    .GetRequiredService<PlatformEventRetentionService>()
                    .RunOnceAsync(stoppingToken);
                if (deleted > 0)
                    logger.LogInformation("Retenção removeu {Count} eventos da plataforma.", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Falha na retenção do EventStore; nova tentativa no próximo ciclo.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
