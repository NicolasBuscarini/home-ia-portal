using GarageWatch.Application.Identity;
using GarageWatch.Application.Monitoring;

namespace GarageWatch.Infrastructure.Identity;

public interface IKnownPeopleGateway
{
    Task<KnownPersonPage> ListAsync(int page, int pageSize, CancellationToken cancellationToken);

    Task<KnownPersonSummary> GetAsync(string publicId, CancellationToken cancellationToken);

    Task<KnownPersonResult> CreateAsync(Guid actorId, string? displayName, string? purpose,
        bool authorizationConfirmed, CancellationToken cancellationToken);

    Task RemoveAsync(Guid actorId, string publicId, CancellationToken cancellationToken);
}

/// <summary>
/// Sends roster mutations to the monitoring service while keeping operator identity and audit
/// in the portal. Audit is written after the remote mutation succeeds; a failure there is
/// surfaced instead of silently losing the trail.
/// </summary>
public sealed class RemoteKnownPeopleGateway(IMonitoringRosterClient client, IIamUnitOfWork unitOfWork,
    IIamAuditEventRepository auditEvents, TimeProvider timeProvider, IBiometricPersonLinkStore links)
    : IKnownPeopleGateway
{
    private const int LookupPageSize = 100;
    private const int LookupMaximumPages = 20;

    public async Task<KnownPersonPage> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 1 || pageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(page), "Paginação fora dos limites.");
        var result = await client.ListAsync(page, pageSize, cancellationToken);
        return new KnownPersonPage(result.Items.Select(Map).ToArray(), result.Page, result.PageSize, result.Total);
    }

    public async Task<KnownPersonSummary> GetAsync(string publicId, CancellationToken cancellationToken)
    {
        if (!PublicIdFormat.IsValid(publicId, PublicIdPrefixes.KnownPersonPrefix))
            throw new KeyNotFoundException("known_person_not_found");
        for (var page = 1; page <= LookupMaximumPages; page++)
        {
            var result = await client.ListAsync(page, LookupPageSize, cancellationToken);
            var match = result.Items.FirstOrDefault(person => person.PublicId == publicId);
            if (match is not null) return Map(match);
            if (result.Items.Count < LookupPageSize || (long)page * LookupPageSize >= result.Total) break;
        }
        throw new KeyNotFoundException("known_person_not_found");
    }

    public async Task<KnownPersonResult> CreateAsync(Guid actorId, string? displayName, string? purpose,
        bool authorizationConfirmed, CancellationToken cancellationToken)
    {
        if (!authorizationConfirmed)
            throw new ArgumentException("A autorização da pessoa deve ser confirmada antes do cadastro.");
        var name = displayName?.Trim();
        var intendedPurpose = purpose?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            throw new ArgumentException("Nome deve ter entre 1 e 128 caracteres.");
        if (string.IsNullOrWhiteSpace(intendedPurpose) || intendedPurpose.Length > 256)
            throw new ArgumentException("Finalidade deve ter entre 1 e 256 caracteres.");

        var created = await client.CreateAsync(new MonitoringRosterCreateRequest(name, intendedPurpose, true),
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        await unitOfWork.ExecuteAtomicallyAsync(token =>
        {
            auditEvents.Append(actorId, null, "known_person.created", created.PublicId, now);
            return Task.FromResult(0);
        }, cancellationToken);
        return new KnownPersonResult(created.PublicId, created.DisplayName, created.Purpose,
            created.AuthorizationRecordedAtUtc, created.CreatedAtUtc, created.Version);
    }

    public async Task RemoveAsync(Guid actorId, string publicId, CancellationToken cancellationToken)
    {
        if (!PublicIdFormat.IsValid(publicId, PublicIdPrefixes.KnownPersonPrefix))
            throw new KeyNotFoundException("Pessoa não encontrada.");
        await client.RemoveAsync(publicId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            await links.UnlinkActiveForPersonAsync(publicId, now, token);
            auditEvents.Append(actorId, null, "known_person.removed", publicId, now);
            return 0;
        }, cancellationToken);
    }

    private static KnownPersonSummary Map(MonitoringRosterPerson person) => new(person.PublicId,
        person.DisplayName, person.Purpose, person.AuthorizationRecordedAtUtc, person.CreatedAtUtc,
        person.Version);
}
