using GarageWatch.Application.PlatformEvents;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

/// <summary>
/// Internal writer for portal actions. It derives category/severity from the catalog, resolves the
/// actor from the authenticated user id and forwards a normalized envelope to the append boundary.
/// Call it inside <c>IamUnitOfWork.ExecuteAtomicallyAsync</c> for mandatory actions: the event is
/// saved in the same transaction and rolls back with the action.
/// </summary>
public sealed class PlatformEventRecorder(
    IamDbContext database,
    IPlatformEventWriter writer,
    IPlatformEventOriginAccessor originAccessor,
    TimeProvider timeProvider,
    IPlatformEventExecutionContext? executionContext = null) : IPlatformEventRecorder
{
    public async Task<PlatformEventWriteResult> RecordAsync(PlatformEventDraft draft,
        CancellationToken cancellationToken = default)
    {
        if (!PlatformEventTypeCatalog.Default.TryGet(draft.EventType, out var definition))
            return PlatformEventWriteResult.Invalid(PlatformEventValidationErrors.UnknownEventType);

        var origin = originAccessor.Current;
        var actorUserId = origin?.ActorUserId ?? executionContext?.Current?.UserId;
        string? actorPublicId = null;
        if (actorUserId is { } resolvedActorUserId)
        {
            actorPublicId = await database.Users
                .Where(user => user.Id == resolvedActorUserId)
                .Select(user => user.PublicId)
                .FirstOrDefaultAsync(cancellationToken);
            if (string.IsNullOrEmpty(actorPublicId))
                return PlatformEventWriteResult.Invalid(PlatformEventValidationErrors.InvalidActor);
        }

        var occurredAtUtc = (draft.OccurredAtUtc ?? timeProvider.GetUtcNow()).ToUniversalTime();
        var value = new PlatformEventV1(
            Guid.NewGuid(),
            draft.EventType,
            definition.Category,
            definition.DefaultSeverity,
            draft.Outcome,
            occurredAtUtc,
            PlatformEventSources.Portal,
            draft.SourceVersion,
            PlatformEventEmitters.Server,
            actorPublicId is null ? null : new PlatformEventActorV1(actorPublicId, null, null),
            draft.Subject,
            draft.Resource,
            origin?.CorrelationId,
            draft.CausationId,
            origin?.RequestId,
            origin?.SessionId,
            origin?.TraceId,
            origin?.SpanId,
            Client: origin?.Client,
            Metadata: draft.Metadata);
        return await writer.WriteAsync(value, cancellationToken);
    }

    public async Task<PlatformEventWriteResult> RecordRequiredAsync(PlatformEventDraft draft,
        CancellationToken cancellationToken = default)
    {
        var result = await RecordAsync(draft, cancellationToken);
        if (result.Status == PlatformEventWriteStatus.Invalid)
            throw new PlatformEventRecordingException(result.Error ?? PlatformEventValidationErrors.InvalidEvent);
        return result;
    }
}
