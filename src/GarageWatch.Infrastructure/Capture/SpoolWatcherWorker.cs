using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Capture;

/// <summary>
/// Alimenta o trigger de clipes com os segmentos gravados pelo serviço de Monitoramento.
/// A captura deixou de acontecer no portal (ADR109), então o spool é a fonte de observação.
/// </summary>
public sealed class SpoolWatcherWorker(
    LocalSegmentSpool spool,
    ClipTriggerWorker trigger,
    ILogger<SpoolWatcherWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private DateTimeOffset _lastSeen = DateTimeOffset.MinValue;
    private bool _initialized;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Observe();
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                logger.LogWarning("Spool de captura indisponível para o trigger; nova tentativa em {Seconds}s",
                    PollInterval.TotalSeconds);
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Observe()
    {
        var recent = spool.GetRecentSegments(64);
        if (!_initialized)
        {
            // Não reprocessa o histórico no restart: evita clipes duplicados fora da janela.
            _lastSeen = recent.Count == 0
                ? DateTimeOffset.UtcNow
                : recent.Max(item => item.Manifest.StartedAt);
            _initialized = true;
            return;
        }

        foreach (var item in recent.OrderBy(entry => entry.Manifest.StartedAt))
        {
            if (item.Manifest.StartedAt <= _lastSeen) continue;
            if (!trigger.TryEnqueue(item.Manifest))
            {
                // Fila cheia: tenta de novo no próximo ciclo sem pular segmentos.
                return;
            }
            _lastSeen = item.Manifest.StartedAt;
        }
    }
}
