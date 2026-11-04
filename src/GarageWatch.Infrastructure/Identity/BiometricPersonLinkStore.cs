using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public interface IBiometricPersonLinkStore
{
    Task<BiometricPersonLink?> GetActiveAsync(string enrollmentPublicId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> GetActiveForEnrollmentsAsync(
        IReadOnlyCollection<string> enrollmentPublicIds, CancellationToken cancellationToken = default);

    Task UnlinkActiveForEnrollmentAsync(string enrollmentPublicId, DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task UnlinkActiveForPersonAsync(string knownPersonPublicId, DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Mutating methods expect the caller to own the unit of work; they only stage changes on tracked rows.
/// </summary>
public sealed class BiometricPersonLinkStore(IamDbContext database) : IBiometricPersonLinkStore
{
    public Task<BiometricPersonLink?> GetActiveAsync(string enrollmentPublicId,
        CancellationToken cancellationToken = default) =>
        database.BiometricPersonLinks.SingleOrDefaultAsync(link =>
            link.EnrollmentPublicId == enrollmentPublicId && link.UnlinkedAtUtc == null, cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> GetActiveForEnrollmentsAsync(
        IReadOnlyCollection<string> enrollmentPublicIds, CancellationToken cancellationToken = default)
    {
        if (enrollmentPublicIds.Count == 0) return new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = await database.BiometricPersonLinks.AsNoTracking()
            .Where(link => enrollmentPublicIds.Contains(link.EnrollmentPublicId) && link.UnlinkedAtUtc == null)
            .Select(link => new { link.EnrollmentPublicId, link.KnownPersonPublicId })
            .ToArrayAsync(cancellationToken);
        return rows.ToDictionary(row => row.EnrollmentPublicId, row => row.KnownPersonPublicId,
            StringComparer.Ordinal);
    }

    public async Task UnlinkActiveForEnrollmentAsync(string enrollmentPublicId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var active = await database.BiometricPersonLinks.SingleOrDefaultAsync(link =>
            link.EnrollmentPublicId == enrollmentPublicId && link.UnlinkedAtUtc == null, cancellationToken);
        if (active is not null) active.UnlinkedAtUtc = now;
    }

    public async Task UnlinkActiveForPersonAsync(string knownPersonPublicId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var active = await database.BiometricPersonLinks
            .Where(link => link.KnownPersonPublicId == knownPersonPublicId && link.UnlinkedAtUtc == null)
            .ToArrayAsync(cancellationToken);
        foreach (var link in active) link.UnlinkedAtUtc = now;
    }
}
