using GarageWatch.Application.PlatformEvents;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public sealed record PlatformEventBackfillResult(int Backfilled, int Pending);

/// <summary>
/// Idempotent reconciliation of legacy IAM audit rows into the platform envelope. It runs after
/// migrations and on every start; rows already backfilled are skipped. The hash is recomputed from
/// the persisted fields so an interrupted run is repaired rather than duplicated.
/// </summary>
public sealed class PlatformEventBackfillService(
    IamDbContext database, IPublicIdGenerator publicIds, TimeProvider timeProvider)
{
    public async Task<PlatformEventBackfillResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var pending = await database.PlatformEvents
            .Where(entry => entry.PublicId == null || entry.PayloadHash == null
                || entry.EventId == Guid.Empty || entry.Category == null)
            .OrderBy(entry => entry.OccurredAtUtc)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0) return new(0, 0);

        var reserved = await database.PlatformEvents
            .Where(entry => entry.PublicId != null)
            .Select(entry => entry.PublicId!)
            .ToHashSetAsync(cancellationToken);
        var backfilled = 0;
        foreach (var entry in pending)
        {
            if (string.IsNullOrEmpty(entry.PublicId))
                entry.PublicId = PublicIdAllocator.GenerateUnique(
                    publicIds, PublicIdPrefixes.EventPrefix, reserved);
            reserved.Add(entry.PublicId);
            if (entry.EventId == Guid.Empty) entry.EventId = entry.Id;
            entry.SchemaVersion ??= PlatformEventContract.SchemaVersion;
            entry.Category ??= PlatformEventCategories.Legacy;
            entry.Severity ??= PlatformEventSeverities.Info;
            entry.Outcome ??= PlatformEventOutcomes.Unknown;
            entry.Source ??= PlatformEventSources.IamLegacy;
            entry.Emitter ??= PlatformEventEmitters.Server;
            entry.RecordedAtUtc ??= entry.OccurredAtUtc;

            var value = ToContract(entry);
            var error = PlatformEventValidator.Validate(value, timeProvider.GetUtcNow());
            if (error is not null)
                throw new InvalidOperationException(
                    $"Evento legado {entry.Id:D} não pode ser migrado ({error}).");
            entry.PayloadHash = PlatformEventCanonicalizer.ComputePayloadHash(value);
            backfilled++;
        }

        await database.SaveChangesAsync(cancellationToken);
        return new(backfilled, 0);
    }

    /// <summary>
    /// Canonical projection used for hashing and integrity checks. Legacy rows have no public actor
    /// or typed resource in the envelope, so those remain outside the hash by design.
    /// </summary>
    public static PlatformEventV1 ToContract(PlatformEvent entry, string? actorPublicId = null) => new(
        entry.EventId == Guid.Empty ? entry.Id : entry.EventId,
        entry.EventType,
        entry.Category ?? PlatformEventCategories.Legacy,
        entry.Severity ?? PlatformEventSeverities.Info,
        entry.Outcome ?? PlatformEventOutcomes.Unknown,
        new DateTimeOffset(entry.OccurredAtUtc.UtcTicks, TimeSpan.Zero),
        entry.Source ?? PlatformEventSources.IamLegacy,
        entry.SourceVersion,
        entry.Emitter ?? PlatformEventEmitters.Server,
        actorPublicId is null ? null : new PlatformEventActorV1(actorPublicId, null, null))
    {
        SchemaVersion = entry.SchemaVersion ?? PlatformEventContract.SchemaVersion
    };
}
