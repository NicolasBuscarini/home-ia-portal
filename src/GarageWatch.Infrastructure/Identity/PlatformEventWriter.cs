using System.Text;
using GarageWatch.Application.PlatformEvents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Identity;

/// <summary>
/// Validates, normalizes and appends platform events. The normalized envelope is what gets hashed
/// and persisted: actor is reduced to the immutable public id, subject is the referenced user and
/// resource is the affected object. That keeps the stored hash reproducible from the columns.
/// </summary>
public sealed class PlatformEventWriter(IamDbContext database, TimeProvider timeProvider,
    ILogger<PlatformEventWriter> logger, PlatformEventMetrics? metrics = null)
    : IPlatformEventWriter
{
    public const string SubjectUserType = "user";

    public async Task<PlatformEventWriteResult> WriteAsync(PlatformEventV1 value,
        CancellationToken cancellationToken = default)
    {
        var validation = PlatformEventValidator.Validate(value, timeProvider.GetUtcNow());
        if (validation is not null)
        {
            metrics?.Rejected(validation);
            return PlatformEventWriteResult.Invalid(validation);
        }

        var actorId = await ResolveUserIdAsync(value.Actor?.UserPublicId, cancellationToken);
        if (value.Actor is not null && actorId is null)
        {
            metrics?.Rejected(PlatformEventValidationErrors.InvalidActor);
            return PlatformEventWriteResult.Invalid(PlatformEventValidationErrors.InvalidActor);
        }

        Guid? subjectId = null;
        if (value.Subject is not null)
        {
            if (!string.Equals(value.Subject.Type, SubjectUserType, StringComparison.Ordinal))
            {
                metrics?.Rejected(PlatformEventValidationErrors.InvalidReference);
                return PlatformEventWriteResult.Invalid(PlatformEventValidationErrors.InvalidReference);
            }
            subjectId = await ResolveUserIdAsync(value.Subject.PublicId, cancellationToken);
            if (subjectId is null)
            {
                metrics?.Rejected(PlatformEventValidationErrors.InvalidReference);
                return PlatformEventWriteResult.Invalid(PlatformEventValidationErrors.InvalidReference);
            }
        }

        var normalized = value with
        {
            Actor = actorId is null
                ? null
                : new PlatformEventActorV1(value.Actor!.UserPublicId, null, null),
            Subject = subjectId is null
                ? null
                : new PlatformEventReferenceV1(SubjectUserType, value.Subject!.PublicId)
        };
        var payloadHash = PlatformEventCanonicalizer.ComputePayloadHash(normalized);

        var existing = await FindAsync(normalized.Source, normalized.EventId, cancellationToken);
        if (existing is not null)
        {
            metrics?.Duplicated();
            return Duplicate(existing, payloadHash, normalized);
        }

        var recordedAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
        var entity = new PlatformEvent
        {
            Id = Guid.NewGuid(),
            EventId = normalized.EventId,
            SchemaVersion = normalized.SchemaVersion,
            EventType = normalized.EventType,
            Category = normalized.Category,
            Severity = normalized.Severity,
            Outcome = normalized.Outcome,
            Source = normalized.Source,
            SourceVersion = normalized.SourceVersion,
            Emitter = normalized.Emitter,
            ActorUserId = actorId,
            SubjectUserId = subjectId,
            ResourceType = normalized.Resource?.Type,
            ResourceId = normalized.Resource?.PublicId,
            CorrelationId = normalized.CorrelationId,
            CausationId = normalized.CausationId,
            RequestId = normalized.RequestId,
            SessionId = normalized.SessionId,
            TraceId = normalized.TraceId,
            SpanId = normalized.SpanId,
            HttpMethod = normalized.Http?.Method,
            HttpRoute = normalized.Http?.Route,
            HttpStatusCode = normalized.Http?.StatusCode,
            DurationMs = normalized.Http?.DurationMs,
            ClientUserAgent = normalized.Client?.UserAgent,
            ClientBrowser = normalized.Client?.Browser,
            ClientOs = normalized.Client?.Os,
            ClientDevice = normalized.Client?.Device,
            ClientIp = normalized.Client?.Ip,
            MetadataJson = normalized.Metadata is null
                ? null
                : Encoding.UTF8.GetString(PlatformEventCanonicalizer.SerializeMetadata(normalized.Metadata)),
            OccurredAtUtc = normalized.OccurredAtUtc.ToUniversalTime(),
            RecordedAtUtc = recordedAtUtc,
            PayloadHash = payloadHash
        };

        database.PlatformEvents.Add(entity);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            database.Entry(entity).State = EntityState.Detached;
            existing = await FindAsync(normalized.Source, normalized.EventId, cancellationToken);
            if (existing is null) throw;
            metrics?.Duplicated();
            return Duplicate(existing, payloadHash, normalized);
        }

        metrics?.Appended(normalized.Source, normalized.Outcome);
        return PlatformEventWriteResult.Created(entity.PublicId!);
    }

    private PlatformEventWriteResult Duplicate(ExistingEvent existing, string payloadHash,
        PlatformEventV1 value)
    {
        if (existing.PayloadHash != payloadHash)
            logger.LogWarning(
                "Evento {Source}/{EventId} reutilizado com payload diferente; mantendo {PublicId}.",
                value.Source, value.EventId, existing.PublicId);
        return PlatformEventWriteResult.Duplicate(existing.PublicId);
    }

    private Task<ExistingEvent?> FindAsync(string source, Guid eventId, CancellationToken cancellationToken) =>
        database.PlatformEvents.AsNoTracking()
            .Where(entry => entry.Source == source && entry.EventId == eventId)
            .Select(entry => new ExistingEvent(entry.PublicId!, entry.PayloadHash!))
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<Guid?> ResolveUserIdAsync(string? publicId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(publicId)) return null;
        return await database.Users
            .Where(user => user.PublicId == publicId)
            .Select(user => (Guid?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private sealed record ExistingEvent(string PublicId, string PayloadHash);
}
