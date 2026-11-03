namespace GarageWatch.Application.Events;

public enum EventAppendStatus
{
    Created,
    Duplicate,
    CapacityReached
}

public sealed record EventJournalRecoveryResult(int RecoveredEvents, int InvalidEvents, long JournalBytes);

public interface IEventJournal
{
    Task<EventJournalRecoveryResult> RecoverAsync(CancellationToken cancellationToken);

    Task<EventAppendStatus> AppendAsync(EventOccurrenceV1 occurrence, CancellationToken cancellationToken);
}
