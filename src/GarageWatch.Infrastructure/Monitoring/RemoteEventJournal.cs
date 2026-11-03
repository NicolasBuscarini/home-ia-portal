using System.Security.Cryptography;
using System.Text.Json;
using GarageWatch.Application.Events;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Events;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Monitoring;

public sealed class RemoteEventJournal(
    IMonitoringEventsClient client,
    ILogger<RemoteEventJournal> logger) : IEventJournal
{
    private static readonly JsonSerializerOptions JsonOptions = EventEngineOptions.CreateJsonOptions();

    public async Task<EventJournalRecoveryResult> RecoverAsync(CancellationToken cancellationToken)
    {
        var recovery = await client.GetRecoveryAsync(cancellationToken);
        if (recovery is null)
        {
            logger.LogWarning("Journal de eventos remoto indisponível durante a recuperação; captura e análise seguem independentes");
            return new EventJournalRecoveryResult(0, 0, 0);
        }
        return new EventJournalRecoveryResult(recovery.RecoveredEvents, recovery.InvalidEvents,
            recovery.JournalBytes);
    }

    public async Task<EventAppendStatus> AppendAsync(EventOccurrenceV1 occurrence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        var payload = JsonSerializer.SerializeToUtf8Bytes(occurrence, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        using var document = JsonDocument.Parse(payload);
        var envelope = new MonitoringEventEnvelope("1.0", hash, document.RootElement.Clone());
        var outcome = await client.AppendAsync(envelope, cancellationToken);
        if (outcome is null) throw new HttpRequestException("monitoring_event_append_unavailable");
        if (outcome.CapacityReached) return EventAppendStatus.CapacityReached;
        return outcome.Status == "created" ? EventAppendStatus.Created : EventAppendStatus.Duplicate;
    }
}
