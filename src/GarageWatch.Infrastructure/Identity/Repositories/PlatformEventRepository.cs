using GarageWatch.Application.Identity;
using GarageWatch.Application.PlatformEvents;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity.Repositories;

public sealed class PlatformEventRepository(IamDbContext database, TimeProvider timeProvider)
    : BaseRepository<PlatformEvent>(database), IPlatformEventRepository
{
    public async Task<PlatformEventRecord?> GetAsync(Guid id, bool withDetails = false,
        CancellationToken cancellationToken = default) =>
        Map(await GetEntityAsync(entry => entry.Id == id, withDetails, cancellationToken));

    public async Task<IReadOnlyList<PlatformEventRecord>> GetAllAsync(bool withDetails = false,
        CancellationToken cancellationToken = default) =>
        (await GetAllEntitiesAsync(withDetails, cancellationToken)).Select(entry => Map(entry)!).ToArray();

    public async Task<RepositoryPage<PlatformEventRecord>> ListPaginatedAsync(int pageNumber, int pageSize,
        bool withDetails = false, CancellationToken cancellationToken = default)
    {
        var result = await ListEntitiesPaginatedAsync(pageNumber, pageSize, withDetails, cancellationToken);
        return new(result.Items.Select(entry => Map(entry)!).ToArray(), pageNumber, pageSize, result.TotalCount);
    }

    /// <summary>
    /// Transitional append used by audited portal actions until T17-C/D migrate them to the typed
    /// writer. The row is a valid legacy-shaped envelope (category <c>legacy</c>), so it is hashable,
    /// filterable and reconcilable without inventing taxonomy.
    /// </summary>
    public void Append(Guid? actorUserId, Guid? subjectUserId, string action, string? resourceId,
        DateTimeOffset occurredAtUtc)
    {
        var id = Guid.NewGuid();
        var occurred = occurredAtUtc.ToUniversalTime();
        var recorded = timeProvider.GetUtcNow().ToUniversalTime();
        var value = new PlatformEventV1(id, action, PlatformEventCategories.Legacy,
            PlatformEventSeverities.Info, PlatformEventOutcomes.Unknown, occurred,
            PlatformEventSources.Portal, null, PlatformEventEmitters.Server);
        var error = PlatformEventValidator.Validate(value, recorded);
        if (error is not null)
            throw new InvalidOperationException($"Evento de auditoria inválido ({error}).");

        AddEntity(new PlatformEvent
        {
            Id = id,
            EventId = id,
            SchemaVersion = PlatformEventContract.SchemaVersion,
            EventType = action,
            Category = PlatformEventCategories.Legacy,
            Severity = PlatformEventSeverities.Info,
            Outcome = PlatformEventOutcomes.Unknown,
            Source = PlatformEventSources.Portal,
            Emitter = PlatformEventEmitters.Server,
            ActorUserId = actorUserId,
            SubjectUserId = subjectUserId,
            ResourceId = resourceId,
            OccurredAtUtc = occurred,
            RecordedAtUtc = recorded,
            PayloadHash = PlatformEventCanonicalizer.ComputePayloadHash(value)
        });
    }

    protected override IQueryable<PlatformEvent> ApplyDetails(IQueryable<PlatformEvent> query) =>
        query.Include(entry => entry.Actor).Include(entry => entry.Subject);

    protected override IOrderedQueryable<PlatformEvent> OrderForPaging(IQueryable<PlatformEvent> query) =>
        query.OrderByDescending(entry => entry.OccurredAtUtc).ThenBy(entry => entry.Id);

    private static PlatformEventRecord? Map(PlatformEvent? entry) => entry is null ? null :
        new(entry.Id, entry.ActorUserId, entry.SubjectUserId, entry.EventType, entry.ResourceId,
            entry.OccurredAtUtc, Map(entry.Actor), Map(entry.Subject), entry.PublicId, entry.EventId,
            entry.Category, entry.Severity, entry.Outcome, entry.Source, entry.PayloadHash,
            entry.RecordedAtUtc);

    private static IdentityUserSummary? Map(ApplicationUser? user) => user is null ? null :
        new(user.Id, user.UserName ?? string.Empty, user.IsActive);
}
