using GarageWatch.Application.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public sealed record BiometricPersonLinkSummary(string EnrollmentPublicId, string KnownPersonPublicId,
    DateTimeOffset LinkedAtUtc);

/// <summary>
/// Links an encrypted enrollment to an authorized roster person without moving biometric payload or
/// roster semantics between the portal and the monitoring service. The link is Admin-only at the endpoint,
/// append-only in effect (old rows become tombstones) and audited on every change.
/// </summary>
public sealed class BiometricPersonLinkService(
    IamDbContext database,
    IIamUnitOfWork unitOfWork,
    IIamAuditEventRepository audit,
    TimeProvider timeProvider,
    IKnownPeopleGateway people,
    IBiometricEnrollmentGateway enrollments)
{
    public async Task<BiometricPersonLinkSummary> LinkAsync(Guid actorId, string enrollmentPublicId,
        string? knownPersonPublicId, CancellationToken cancellationToken)
    {
        if (!PublicIdFormat.IsValid(enrollmentPublicId, PublicIdPrefixes.BiometricEnrollmentPrefix))
            throw new KeyNotFoundException("biometric_enrollment_not_found");
        if (!PublicIdFormat.IsValid(knownPersonPublicId, PublicIdPrefixes.KnownPersonPrefix))
            throw new ArgumentException("invalid_known_person", nameof(knownPersonPublicId));

        var enrollment = await enrollments.GetForAdministrationAsync(enrollmentPublicId, cancellationToken);
        if (enrollment.Status != "active")
            throw new ArgumentException("biometric_enrollment_inactive", nameof(enrollmentPublicId));
        _ = await people.GetAsync(knownPersonPublicId!, cancellationToken);

        var now = timeProvider.GetUtcNow();
        return await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            var active = await database.BiometricPersonLinks.SingleOrDefaultAsync(link =>
                link.EnrollmentPublicId == enrollmentPublicId && link.UnlinkedAtUtc == null, token);
            if (active is not null && active.KnownPersonPublicId == knownPersonPublicId)
                return Map(active);
            if (active is not null)
            {
                active.UnlinkedAtUtc = now;
                audit.Append(actorId, null, "biometric.person_unlinked", enrollmentPublicId, now);
            }

            var link = new BiometricPersonLink
            {
                Id = Guid.NewGuid(),
                EnrollmentPublicId = enrollmentPublicId,
                KnownPersonPublicId = knownPersonPublicId!,
                LinkedByUserId = actorId,
                LinkedAtUtc = now
            };
            database.BiometricPersonLinks.Add(link);
            audit.Append(actorId, null, "biometric.person_linked", enrollmentPublicId, now);
            return Map(link);
        }, cancellationToken);
    }

    public async Task UnlinkAsync(Guid actorId, string enrollmentPublicId, CancellationToken cancellationToken)
    {
        if (!PublicIdFormat.IsValid(enrollmentPublicId, PublicIdPrefixes.BiometricEnrollmentPrefix))
            throw new KeyNotFoundException("biometric_enrollment_not_found");
        var now = timeProvider.GetUtcNow();
        await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            var active = await database.BiometricPersonLinks.SingleOrDefaultAsync(link =>
                link.EnrollmentPublicId == enrollmentPublicId && link.UnlinkedAtUtc == null, token);
            if (active is null) return false;
            active.UnlinkedAtUtc = now;
            audit.Append(actorId, null, "biometric.person_unlinked", enrollmentPublicId, now);
            return true;
        }, cancellationToken);
    }

    private static BiometricPersonLinkSummary Map(BiometricPersonLink link) =>
        new(link.EnrollmentPublicId, link.KnownPersonPublicId, link.LinkedAtUtc);
}
