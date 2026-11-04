using System.Security.Cryptography;
using GarageWatch.Application.Biometrics;
using GarageWatch.Application.Identity;
using GarageWatch.Application.Monitoring;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Identity;

/// <summary>
/// Reads encrypted enrollments from the monitoring service and filters them by the portal-owned active
/// roster link. The service never receives the key or plaintext; decryption, comparison and cleanup
/// stay in the portal. Unavailable or corrupt payloads fail closed or are skipped and counted.
/// </summary>
public sealed class RemoteIdentityEmbeddingSource(IMonitoringBiometricCiphertextClient client,
    IBiometricDataProtector protector, IBiometricPersonLinkStore links, TimeProvider timeProvider,
    ILogger<RemoteIdentityEmbeddingSource> logger) : IIdentityEmbeddingSource
{
    public async Task<IdentityEmbeddingSnapshot> LoadAsync(FaceObservationV1 observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!observation.IsValid) throw new ArgumentException("invalid_face_observation", nameof(observation));
        if (!protector.IsConfigured)
            throw new IdentityMatchingUnavailableException("biometric_protection_not_configured");

        IReadOnlyList<MonitoringBiometricCiphertext> envelopes;
        try
        {
            envelopes = await client.ListActiveAsync(cancellationToken);
        }
        catch (MonitoringBiometricsUnavailableException)
        {
            throw new IdentityMatchingUnavailableException("biometrics_store_unavailable");
        }

        var activeLinks = await links.GetActiveForEnrollmentsAsync(
            envelopes.Select(envelope => envelope.PublicId).ToArray(), cancellationToken);
        var now = timeProvider.GetUtcNow();
        var candidates = new List<IdentityEmbeddingCandidate>(envelopes.Count);
        var skipped = 0;
        foreach (var envelope in envelopes)
        {
            if (!activeLinks.TryGetValue(envelope.PublicId, out var personPublicId)) continue;
            if (envelope.ExpiresAtUtc <= now ||
                envelope.EmbeddingCiphertext is not { Length: > 0 } ||
                !string.Equals(envelope.ModelId, observation.ModelId, StringComparison.Ordinal) ||
                !string.Equals(envelope.ModelVersion, observation.ModelVersion, StringComparison.Ordinal) ||
                !string.Equals(envelope.EmbeddingSchemaVersion, observation.EmbeddingSchemaVersion,
                    StringComparison.Ordinal) ||
                envelope.Dimensions != observation.Dimensions)
            {
                skipped++;
                continue;
            }

            byte[]? plaintext = null;
            try
            {
                plaintext = protector.Unprotect(new ProtectedBiometricEmbedding(
                    envelope.EmbeddingCiphertext, envelope.EmbeddingNonce, envelope.EmbeddingTag,
                    envelope.KeyVersion));
                var values = BiometricEmbeddingCodec.Deserialize(plaintext, envelope.Dimensions);
                if (values is null)
                {
                    skipped++;
                    continue;
                }
                candidates.Add(new IdentityEmbeddingCandidate(personPublicId, envelope.PublicId,
                    envelope.ModelId, envelope.ModelVersion, envelope.EmbeddingSchemaVersion,
                    envelope.Dimensions, values));
            }
            catch (Exception exception) when (exception is CryptographicException or InvalidOperationException)
            {
                skipped++;
                logger.LogWarning("Inscrição biométrica remota ignorada no matching: motivo={Reason}",
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
