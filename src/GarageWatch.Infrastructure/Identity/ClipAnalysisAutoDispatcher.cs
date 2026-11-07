using GarageWatch.Infrastructure.Capture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Identity;

public interface IClipAnalysisAutoDispatcher
{
    Task DispatchAsync(ClipManifestV1 clip, CancellationToken cancellationToken);
}

public sealed class ClipAnalysisAutoDispatcher(IServiceScopeFactory scopes) : IClipAnalysisAutoDispatcher
{
    public async Task DispatchAsync(ClipManifestV1 clip, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ClipAnalysisDispatchService>()
            .AutoRequestAsync(clip, cancellationToken);
    }
}

/// <summary>One bounded startup reconciliation for clips assembled before a restart.</summary>
public sealed class ClipAnalysisReconciliationWorker(IServiceScopeFactory scopes,
    ILogger<ClipAnalysisReconciliationWorker> logger) : BackgroundService
{
    private const int RecentClipLimit = 50;
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var service = scope.ServiceProvider.GetRequiredService<ClipAnalysisDispatchService>();
                    var enqueued = await service.ReconcileRecentAsync(RecentClipLimit, stoppingToken);
                    if (enqueued > 0)
                        logger.LogInformation("Reconciliação de análise enfileirou {Count} clipes recentes.",
                            enqueued);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    logger.LogWarning("Reconciliação de análise indisponível; motivo={FailureType}",
                        exception.GetType().Name);
                }
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
