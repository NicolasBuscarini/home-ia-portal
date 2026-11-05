using GarageWatch.Application.Identity;
using GarageWatch.Application.PlatformEvents;
using GarageWatch.Application.Portal;
using GarageWatch.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Integrations;

/// <summary>
/// Portal-owned encrypted integration credentials (P32). Secrets are only returned to server-side
/// consumers; status projections contain metadata only. Reads re-seal rows written with a previous
/// key version once the active key is available, and every mutation is audited.
/// </summary>
public sealed class IntegrationCredentialStore(
    IamDbContext database,
    IIamUnitOfWork unitOfWork,
    IPlatformEventRecorder recorder,
    IIntegrationCredentialProtector protector,
    TimeProvider timeProvider,
    ILogger<IntegrationCredentialStore> logger) : IIntegrationCredentialStore
{
    private const int MaxSecretLength = 512;

    public async Task<ResolvedIntegrationCredential?> ResolveAsync(string integrationId,
        CancellationToken cancellationToken = default)
    {
        var normalized = IntegrationCredentialIds.Normalize(integrationId);
        var entity = await database.IntegrationCredentials
            .SingleOrDefaultAsync(credential => credential.IntegrationId == normalized, cancellationToken);
        if (entity is null) return null;

        var plaintext = protector.TryOpen(entity.Ciphertext, entity.Nonce, entity.Tag, entity.KeyVersion, normalized);
        if (plaintext is null)
            throw new IntegrationCredentialUnreadableException(normalized);

        if (protector.IsConfigured && entity.KeyVersion != protector.ActiveKeyVersion)
            return await ReSealAsync(entity, normalized, plaintext, cancellationToken);
        return new(plaintext, entity.KeyVersion);
    }

    public async Task<IntegrationCredentialStatus> GetStatusAsync(string integrationId,
        CancellationToken cancellationToken = default)
    {
        var normalized = IntegrationCredentialIds.Normalize(integrationId);
        var entity = await database.IntegrationCredentials.AsNoTracking()
            .SingleOrDefaultAsync(credential => credential.IntegrationId == normalized, cancellationToken);
        return entity is null
            ? new(normalized, false, null, null, 0)
            : new(normalized, true, entity.KeyVersion, entity.UpdatedAtUtc, entity.Version);
    }

    public Task<IntegrationCredentialStatus> SetAsync(Guid actorUserId, string integrationId, string secret,
        CancellationToken cancellationToken = default)
    {
        var normalized = IntegrationCredentialIds.Normalize(integrationId);
        ValidateSecret(secret);
        if (!protector.IsConfigured) throw new IntegrationCredentialEncryptionUnavailableException();
        var sealedCredential = protector.Seal(secret, normalized);
        return unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            var now = timeProvider.GetUtcNow();
            var entity = await database.IntegrationCredentials
                .SingleOrDefaultAsync(credential => credential.IntegrationId == normalized, token);
            if (entity is null)
            {
                entity = new IntegrationCredential
                {
                    Id = Guid.NewGuid(),
                    IntegrationId = normalized,
                    CreatedAtUtc = now,
                    Version = 0
                };
                database.IntegrationCredentials.Add(entity);
            }
            entity.KeyVersion = sealedCredential.KeyVersion;
            entity.Nonce = sealedCredential.Nonce;
            entity.Ciphertext = sealedCredential.Ciphertext;
            entity.Tag = sealedCredential.Tag;
            entity.UpdatedAtUtc = now;
            entity.UpdatedByUserId = actorUserId;
            entity.Version++;
            await recorder.RecordRequiredAsync(new PlatformEventDraft(
                "integration_credential.updated", PlatformEventOutcomes.Success,
                Resource: new PlatformEventReferenceV1("integration", normalized), OccurredAtUtc: now), token);
            return new IntegrationCredentialStatus(normalized, true, entity.KeyVersion, now, entity.Version);
        }, cancellationToken);
    }

    public Task<IntegrationCredentialStatus> RemoveAsync(Guid actorUserId, string integrationId,
        CancellationToken cancellationToken = default)
    {
        var normalized = IntegrationCredentialIds.Normalize(integrationId);
        return unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            var entity = await database.IntegrationCredentials
                .SingleOrDefaultAsync(credential => credential.IntegrationId == normalized, token);
            if (entity is null) return new IntegrationCredentialStatus(normalized, false, null, null, 0);
            database.IntegrationCredentials.Remove(entity);
            await recorder.RecordRequiredAsync(new PlatformEventDraft(
                "integration_credential.removed", PlatformEventOutcomes.Success,
                Resource: new PlatformEventReferenceV1("integration", normalized),
                OccurredAtUtc: timeProvider.GetUtcNow()), token);
            return new IntegrationCredentialStatus(normalized, false, null, null, 0);
        }, cancellationToken);
    }

    private async Task<ResolvedIntegrationCredential> ReSealAsync(IntegrationCredential entity,
        string integrationId, string plaintext, CancellationToken cancellationToken)
    {
        try
        {
            var sealedCredential = protector.Seal(plaintext, integrationId);
            entity.KeyVersion = sealedCredential.KeyVersion;
            entity.Nonce = sealedCredential.Nonce;
            entity.Ciphertext = sealedCredential.Ciphertext;
            entity.Tag = sealedCredential.Tag;
            entity.UpdatedAtUtc = timeProvider.GetUtcNow();
            entity.Version++;
            await recorder.RecordRequiredAsync(new PlatformEventDraft(
                "integration_credential.rotated", PlatformEventOutcomes.Success,
                Resource: new PlatformEventReferenceV1("integration", integrationId),
                OccurredAtUtc: entity.UpdatedAtUtc), cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            return new(plaintext, entity.KeyVersion);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Rotação concorrente de credencial de integração ignorada para {IntegrationId}.",
                integrationId);
            return new(plaintext, entity.KeyVersion);
        }
    }

    private static void ValidateSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > MaxSecretLength ||
            secret.Any(char.IsControl))
            throw new IntegrationCredentialInputException("invalid_integration_credential");
    }
}
