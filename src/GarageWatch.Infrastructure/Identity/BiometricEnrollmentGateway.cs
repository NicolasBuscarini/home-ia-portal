using System.Buffers.Binary;
using GarageWatch.Application.Analysis;
using GarageWatch.Application.Biometrics;
using GarageWatch.Application.Identity;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Identity.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Identity;

public interface IBiometricEnrollmentGateway
{
    Task<IReadOnlyList<BiometricEnrollmentSummary>> ListAsync(Guid actorId, string? userPublicId,
        CancellationToken cancellationToken);

    Task<BiometricEnrollmentSummary> GetForAdministrationAsync(string enrollmentPublicId,
        CancellationToken cancellationToken);

    Task<BiometricEnrollmentSummary> EnrollAsync(Guid actorId, string? userPublicId, FaceCropV1 crop,
        string? consentVersion, CancellationToken cancellationToken);

    Task RevokeAsync(Guid actorId, string enrollmentPublicId, CancellationToken cancellationToken);
}

/// <summary>Local ownership path; identity, extraction, encryption, persistence and audit stay in the portal.</summary>
public sealed class LocalBiometricEnrollmentGateway(BiometricEnrollmentService service) : IBiometricEnrollmentGateway
{
    public Task<IReadOnlyList<BiometricEnrollmentSummary>> ListAsync(Guid actorId, string? userPublicId,
        CancellationToken cancellationToken) => service.ListAsync(actorId, userPublicId, cancellationToken);

    public Task<BiometricEnrollmentSummary> GetForAdministrationAsync(string enrollmentPublicId,
        CancellationToken cancellationToken) =>
        service.GetForAdministrationAsync(enrollmentPublicId, cancellationToken);

    public Task<BiometricEnrollmentSummary> EnrollAsync(Guid actorId, string? userPublicId, FaceCropV1 crop,
        string? consentVersion, CancellationToken cancellationToken) =>
        service.EnrollAsync(actorId, userPublicId, crop, consentVersion, cancellationToken);

    public Task RevokeAsync(Guid actorId, string enrollmentPublicId, CancellationToken cancellationToken) =>
        service.RevokeAsync(actorId, enrollmentPublicId, cancellationToken);
}

/// <summary>
/// Remote ownership path: the portal keeps account resolution, permission checks, extraction, encryption,
/// consent text and audit; only ciphertext and non-biometric metadata cross the loopback boundary.
/// </summary>
public sealed class RemoteBiometricEnrollmentGateway(IamDbContext database, AccessProfileService profiles,
    IBiometricDataProtector protector, IEnumerable<IFaceEnrollmentExtractor> extractors,
    IMonitoringBiometricClient client, IIamUnitOfWork unitOfWork, IIamAuditEventRepository audit,
    TimeProvider timeProvider, ILogger<RemoteBiometricEnrollmentGateway> logger,
    IBiometricPersonLinkStore links) : IBiometricEnrollmentGateway
{
    public async Task<IReadOnlyList<BiometricEnrollmentSummary>> ListAsync(Guid actorId, string? userPublicId,
        CancellationToken cancellationToken)
    {
        var target = await ResolveTargetAsync(actorId, userPublicId, cancellationToken);
        try
        {
            var items = await client.ListAsync(target.PublicId!, cancellationToken);
            return items.Select(Map).ToArray();
        }
        catch (MonitoringBiometricsUnavailableException)
        {
            throw new BiometricEnrollmentUnavailableException("biometrics_store_unavailable");
        }
    }

    public async Task<BiometricEnrollmentSummary> GetForAdministrationAsync(string enrollmentPublicId,
        CancellationToken cancellationToken)
    {
        if (!PublicIdFormat.IsValid(enrollmentPublicId, PublicIdPrefixes.BiometricEnrollmentPrefix))
            throw new KeyNotFoundException("biometric_enrollment_not_found");
        try
        {
            return Map(await client.GetAsync(enrollmentPublicId, cancellationToken));
        }
        catch (MonitoringBiometricsRejectedException)
        {
            throw new KeyNotFoundException("biometric_enrollment_not_found");
        }
        catch (MonitoringBiometricsUnavailableException)
        {
            throw new BiometricEnrollmentUnavailableException("biometrics_store_unavailable");
        }
    }

    public async Task<BiometricEnrollmentSummary> EnrollAsync(Guid actorId, string? userPublicId, FaceCropV1 crop,
        string? consentVersion, CancellationToken cancellationToken)
    {
        if (!crop.IsValid) throw new ArgumentException("Recorte facial inválido.");
        if (!string.Equals(consentVersion, BiometricPolicy.ConsentVersion, StringComparison.Ordinal))
            throw new BiometricConsentRequiredException();
        var extractor = extractors.FirstOrDefault()
            ?? throw new BiometricEnrollmentUnavailableException("face_extractor_not_configured");
        if (!protector.IsConfigured)
            throw new BiometricEnrollmentUnavailableException("biometric_protection_not_configured");
        var target = await ResolveTargetAsync(actorId, userPublicId, cancellationToken);
        if (!target.IsActive) throw new ArgumentException("Conta de destino inativa.");

        FaceEmbeddingV1 embedding;
        try
        {
            embedding = await extractor.ExtractAsync(crop, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FaceExtractionException exception)
        {
            if (exception.Code == "face_extractor_not_configured")
                throw new BiometricEnrollmentUnavailableException("face_extractor_not_configured");
            throw new BiometricEnrollmentRejectedException(exception.Code);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Falha inesperada na extração facial; tipo={ExceptionType}", exception.GetType().Name);
            throw new BiometricEnrollmentUnavailableException("face_extraction_failed");
        }
        if (!embedding.IsValid) throw new ArgumentException("Embedding facial inválido.");

        var protectedData = protector.Protect(Serialize(embedding.Values));
        MonitoringBiometricEnrollmentResult result;
        try
        {
            result = await client.EnrollAsync(new MonitoringBiometricEnrollRequest(target.PublicId!,
                protectedData.Ciphertext, protectedData.Nonce, protectedData.Tag, protectedData.KeyVersion,
                embedding.Dimensions, embedding.ModelId, embedding.ModelVersion, embedding.SchemaVersion,
                extractor.AdapterId, BiometricPolicy.ConsentVersion), cancellationToken);
        }
        catch (MonitoringBiometricsRejectedException exception)
        {
            throw new BiometricEnrollmentRejectedException(exception.Message);
        }
        catch (MonitoringBiometricsUnavailableException)
        {
            throw new BiometricEnrollmentUnavailableException("biometrics_store_unavailable");
        }

        var now = timeProvider.GetUtcNow();
        await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            foreach (var replaced in result.ReplacedPublicIds)
            {
                await links.UnlinkActiveForEnrollmentAsync(replaced, now, token);
                audit.Append(actorId, target.Id, "biometric.enrollment_revoked", replaced, now);
            }
            audit.Append(actorId, target.Id, "biometric.enrollment_created", result.Enrollment.PublicId, now);
            return true;
        }, cancellationToken);
        return Map(result.Enrollment);
    }

    public async Task RevokeAsync(Guid actorId, string enrollmentPublicId, CancellationToken cancellationToken)
    {
        if (!PublicIdFormat.IsValid(enrollmentPublicId, PublicIdPrefixes.BiometricEnrollmentPrefix))
            throw new KeyNotFoundException();
        MonitoringBiometricEnrollment enrollment;
        try
        {
            enrollment = await client.GetAsync(enrollmentPublicId, cancellationToken);
        }
        catch (MonitoringBiometricsRejectedException)
        {
            throw new KeyNotFoundException();
        }
        catch (MonitoringBiometricsUnavailableException)
        {
            throw new BiometricEnrollmentUnavailableException("biometrics_store_unavailable");
        }

        var actor = await database.Users.AsNoTracking().SingleAsync(item => item.Id == actorId, cancellationToken);
        var isOwner = string.Equals(enrollment.UserPublicId, actor.PublicId, StringComparison.Ordinal);
        if (!isOwner && !await CanManageAsync(actorId, cancellationToken))
            throw new UnauthorizedAccessException();
        var reason = isOwner ? "owner_request" : "administrator_request";
        try
        {
            await client.RevokeAsync(enrollmentPublicId, reason, cancellationToken);
        }
        catch (MonitoringBiometricsRejectedException)
        {
            throw new ArgumentException("Cadastro biométrico inválido.");
        }
        catch (MonitoringBiometricsUnavailableException)
        {
            throw new BiometricEnrollmentUnavailableException("biometrics_store_unavailable");
        }

        var subject = await database.Users.AsNoTracking()
            .SingleOrDefaultAsync(item => item.PublicId == enrollment.UserPublicId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            await links.UnlinkActiveForEnrollmentAsync(enrollmentPublicId, now, token);
            audit.Append(actorId, subject?.Id, "biometric.enrollment_revoked", enrollmentPublicId, now);
            return true;
        }, cancellationToken);
    }

    private async Task<ApplicationUser> ResolveTargetAsync(Guid actorId, string? userPublicId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userPublicId))
            return await database.Users.SingleAsync(item => item.Id == actorId, cancellationToken);
        if (!PublicIdFormat.IsValid(userPublicId, PublicIdPrefixes.UserPrefix)) throw new KeyNotFoundException();
        var target = await database.Users.SingleOrDefaultAsync(item => item.PublicId == userPublicId, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (target.Id != actorId && !await CanManageAsync(actorId, cancellationToken))
            throw new UnauthorizedAccessException();
        return target;
    }

    private Task<bool> CanManageAsync(Guid actorId, CancellationToken cancellationToken) =>
        profiles.IsAllowedAnyCameraAsync(actorId, BiometricEnrollmentService.ManagePermission, cancellationToken);

    private static BiometricEnrollmentSummary Map(MonitoringBiometricEnrollment item) => new(item.PublicId,
        item.UserPublicId, item.Status, item.EnrolledAtUtc, item.ExpiresAtUtc, item.RevokedAtUtc, item.RevokedReason,
        item.ModelId, item.ModelVersion, item.Dimensions, item.KeyVersion, item.ConsentVersion,
        item.ConsentAcceptedAtUtc);

    private static byte[] Serialize(IReadOnlyList<float> values)
    {
        var bytes = new byte[values.Count * sizeof(float)];
        for (var index = 0; index < values.Count; index++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float)), values[index]);
        return bytes;
    }
}
