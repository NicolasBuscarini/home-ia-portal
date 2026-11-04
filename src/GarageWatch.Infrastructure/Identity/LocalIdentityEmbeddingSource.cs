using System.Security.Cryptography;
using GarageWatch.Application.Biometrics;
using GarageWatch.Application.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Identity;

/// <summary>
/// Reads active portal-owned enrollments that have an active roster link, decrypts each embedding in
/// memory and discards rows whose model/schema/dimensions do not match the observation. Missing keys
/// fail closed; individually corrupt rows are skipped and counted. No image, name or audit payload is read.
/// </summary>
public sealed class LocalIdentityEmbeddingSource(IamDbContext database, IBiometricDataProtector protector,
    IConfiguration configuration, TimeProvider timeProvider,
    ILogger<LocalIdentityEmbeddingSource> logger) : IIdentityEmbeddingSource
{
    public async Task<IdentityEmbeddingSnapshot> LoadAsync(FaceObservationV1 observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!observation.IsValid) throw new ArgumentException("invalid_face_observation", nameof(observation));
        if (!string.Equals(configuration["MONITORING_BIOMETRICS_SOURCE"], "local",
                StringComparison.OrdinalIgnoreCase))
            throw new IdentityMatchingUnavailableException("identity_ciphertext_source_unavailable");
        if (!protector.IsConfigured)
            throw new IdentityMatchingUnavailableException("biometric_protection_not_configured");

        var now = timeProvider.GetUtcNow();
        var rows = await (from enrollment in database.BiometricEnrollments.AsNoTracking()
                          join link in database.BiometricPersonLinks.AsNoTracking()
                              on enrollment.PublicId equals link.EnrollmentPublicId
                          where enrollment.PublicId != null &&
                                enrollment.Status == BiometricEnrollment.ActiveStatus &&
                                enrollment.ExpiresAtUtc > now &&
                                link.UnlinkedAtUtc == null
                          select new { Enrollment = enrollment, link.KnownPersonPublicId })
            .ToArrayAsync(cancellationToken);

        var candidates = new List<IdentityEmbeddingCandidate>(rows.Length);
        var skipped = 0;
        foreach (var row in rows)
        {
            var enrollment = row.Enrollment;
            if (!string.Equals(enrollment.ModelId, observation.ModelId, StringComparison.Ordinal) ||
                !string.Equals(enrollment.ModelVersion, observation.ModelVersion, StringComparison.Ordinal) ||
                !string.Equals(enrollment.SchemaVersion, observation.EmbeddingSchemaVersion, StringComparison.Ordinal) ||
                enrollment.Dimensions != observation.Dimensions)
            {
                skipped++;
                continue;
            }

            byte[]? plaintext = null;
            try
            {
                plaintext = protector.Unprotect(new ProtectedBiometricEmbedding(
                    enrollment.EmbeddingCiphertext, enrollment.EmbeddingNonce, enrollment.EmbeddingTag,
                    enrollment.KeyVersion));
                var values = BiometricEmbeddingCodec.Deserialize(plaintext, enrollment.Dimensions);
                if (values is null)
                {
                    skipped++;
                    continue;
                }
                candidates.Add(new IdentityEmbeddingCandidate(row.KnownPersonPublicId,
                    enrollment.PublicId!, enrollment.ModelId, enrollment.ModelVersion,
                    enrollment.SchemaVersion, enrollment.Dimensions, values));
            }
            catch (Exception exception) when (exception is CryptographicException or InvalidOperationException)
            {
                skipped++;
                logger.LogWarning("Inscrição biométrica ignorada no matching: motivo={Reason}",
                    exception.GetType().Name);
            }
            finally
            {
                if (plaintext is not null) Array.Clear(plaintext);
            }
        }

        return new IdentityEmbeddingSnapshot(candidates, skipped);
    }
}
