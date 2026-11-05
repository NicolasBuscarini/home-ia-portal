using GarageWatch.Application.Identity;
using GarageWatch.Application.PlatformEvents;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity.Repositories;

public sealed class PlatformEventRepository(IamDbContext database)
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
    /// Append-only read boundary. All audited writes go through <see cref="IPlatformEventRecorder"/>
    /// (typed envelope, actor/subject by public reference); the compatibility append no longer exists,
    /// so rows are either typed or preserved backfilled history.
    /// </summary>
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
