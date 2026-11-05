using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Identity;

/// <summary>
/// Removes identity observations and their reviews after the configured TTL. Candidate rows and
/// reviews are removed by the configured cascade; no embedding or image exists in this store.
/// </summary>
public sealed class IdentityObservationRetentionService(
    IamDbContext database, IdentityObservationRetentionOptions options, TimeProvider timeProvider)
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Enabled) return 0;

        var cutoff = timeProvider.GetUtcNow().ToUniversalTime().AddDays(-options.RetentionDays);
        var deleted = 0;
        while (true)
        {
            var batch = await database.IdentityObservations
                .Where(item => item.IngestedAtUtc < cutoff)
                .OrderBy(item => item.IngestedAtUtc)
                .Take(options.BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0) break;

            database.IdentityObservations.RemoveRange(batch);
            await database.SaveChangesAsync(cancellationToken);
            deleted += batch.Count;
            if (batch.Count < options.BatchSize) break;
        }

        return deleted;
    }
}

public sealed class IdentityObservationRetentionWorker(
    IServiceScopeFactory scopeFactory,
    IdentityObservationRetentionOptions options,
    ILogger<IdentityObservationRetentionWorker> logger) : BackgroundService
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
                    .GetRequiredService<IdentityObservationRetentionService>()
                    .RunOnceAsync(stoppingToken);
                if (deleted > 0)
                    logger.LogInformation("Retenção removeu {Count} observações de identidade.", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Falha na retenção de observações de identidade; nova tentativa no próximo ciclo.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
