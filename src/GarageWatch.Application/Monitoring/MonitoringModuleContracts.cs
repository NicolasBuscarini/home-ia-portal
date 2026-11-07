using System.Text.Json;

namespace GarageWatch.Application.Monitoring;

public sealed record MonitoringModuleContract(
    string SchemaVersion,
    string ModuleId,
    string ContractVersion,
    string ExecutionMode,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset? GeneratedAtUtc,
    bool Reachable,
    string? ErrorCode);

public sealed record MonitoringCameraStatus(
    string SchemaVersion,
    string ModuleId,
    string CameraId,
    string Status,
    string? StreamEpoch,
    long Segments,
    long Gaps,
    DateTimeOffset? LastSegmentEndedAtUtc,
    string? ReasonCode,
    bool Reachable,
    string? ErrorCode);

public sealed record MonitoringCaptureSnapshot(
    string SchemaVersion,
    string Profile,
    string CameraId,
    bool Enabled,
    int SegmentSeconds,
    int MaximumSegments,
    long MaximumSegmentBytes,
    long Revision,
    string State,
    string Adapter,
    string? LastErrorCode,
    long Gaps,
    int Segments);

public sealed record MonitoringCaptureSettings(
    bool Enabled,
    int SegmentSeconds,
    int MaximumSegments,
    long MaximumSegmentBytes,
    long Revision);

public interface IMonitoringModuleClient
{
    Task<MonitoringModuleContract> GetContractAsync(CancellationToken cancellationToken = default);

    Task<MonitoringCameraStatus> GetCameraStatusAsync(string cameraId,
        CancellationToken cancellationToken = default);
}

public interface IMonitoringClipsClient
{
    Task<MonitoringClipPage?> ListAsync(MonitoringClipQuery query,
        CancellationToken cancellationToken = default);

    Task<MonitoringClipItem?> GetAsync(string publicId, CancellationToken cancellationToken = default);

    Task<MonitoringClipDeleteResult> DeleteAsync(string publicId,
        CancellationToken cancellationToken = default);

    Task<MonitoringClipDeleteAllResult?> DeleteAllAsync(CancellationToken cancellationToken = default);

    Task<HttpResponseMessage?> GetThumbnailAsync(string publicId,
        CancellationToken cancellationToken = default);

    Task<HttpResponseMessage?> GetMediaAsync(string publicId, string? range,
        CancellationToken cancellationToken = default);
}

public sealed record MonitoringClipQuery(IReadOnlyList<string> CameraIds, string? PublicId,
    DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, string? State, int? MinDurationSeconds,
    int? MaxDurationSeconds, string? Analysis, bool? Preserved, string? Sort, int Page, int PageSize);

public sealed record MonitoringClipItem(string PublicId, string ClipId, string CameraId,
    DateTimeOffset StartedAtUtc, DateTimeOffset EndedAtUtc, string State, string ArchiveStatus,
    string TriggerReason, string TriggerConfigVersion, string? MediaSha256, string? ThumbnailSha256,
    int AttemptCount, DateTimeOffset UpdatedAtUtc, long? SizeBytes);

public sealed record MonitoringClipPage(IReadOnlyList<MonitoringClipItem> Items, int Page, int PageSize, int Total);

public enum MonitoringClipDeleteStatus { Deleted, NotFound, Preserved, Archived, NotReady, Failed, Unavailable }

public sealed record MonitoringClipDeleteResult(MonitoringClipDeleteStatus Status, string? Error = null);

public sealed record MonitoringClipDeleteAllResult(int Deleted, int SkippedPreserved, int SkippedArchived,
    int SkippedNotReady, int SkippedActiveAnalysis, int Failed);

public interface IMonitoringCaptureClient
{
    Task<MonitoringCaptureSnapshot?> GetCaptureAsync(CancellationToken cancellationToken = default);

    Task<MonitoringCaptureSettings?> UpdateCaptureAsync(MonitoringCaptureSettings settings,
        CancellationToken cancellationToken = default);
}

public interface IMonitoringEventsClient
{
    Task<MonitoringEventAppendOutcome?> AppendAsync(MonitoringEventEnvelope envelope,
        CancellationToken cancellationToken = default);

    Task<MonitoringEventRecovery?> GetRecoveryAsync(CancellationToken cancellationToken = default);
}

public sealed record MonitoringEventEnvelope(string SchemaVersion, string EventSha256, JsonElement Event);

public sealed record MonitoringEventAppendOutcome(string Status, bool CapacityReached);

public sealed record MonitoringEventRecovery(int RecoveredEvents, int InvalidEvents, long JournalBytes);

public interface IMonitoringRosterClient
{
    Task<MonitoringRosterPage> ListAsync(int page, int pageSize,
        CancellationToken cancellationToken = default);

    Task<MonitoringRosterPerson> CreateAsync(MonitoringRosterCreateRequest request,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(string publicId, CancellationToken cancellationToken = default);
}

public sealed record MonitoringRosterCreateRequest(string DisplayName, string Purpose, bool AuthorizationConfirmed);

public sealed record MonitoringRosterPerson(string PublicId, string DisplayName, string Purpose,
    DateTimeOffset AuthorizationRecordedAtUtc, DateTimeOffset CreatedAtUtc, long Version);

public sealed record MonitoringRosterPage(IReadOnlyList<MonitoringRosterPerson> Items, int Page, int PageSize,
    int Total);

public sealed class MonitoringRosterUnavailableException()
    : Exception("monitoring_roster_unavailable");

public interface IMonitoringRetentionClient
{
    Task<MonitoringRetentionPolicy?> GetPolicyAsync(string cameraId,
        CancellationToken cancellationToken = default);

    Task<MonitoringRetentionPolicy> UpdatePolicyAsync(string cameraId, long expectedVersion, int retentionDays,
        int? maximumStorageGigabytes, CancellationToken cancellationToken = default);

    Task<MonitoringPreservation> SetPreservationAsync(string clipPublicId, long expectedVersion, bool isPreserved,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MonitoringPreservation>> ListPreservationsAsync(
        IReadOnlyCollection<string> clipPublicIds, CancellationToken cancellationToken = default);
}

public sealed record MonitoringRetentionPolicy(string CameraId, int RetentionDays,
    int? MaximumStorageGigabytes, long Version, DateTimeOffset? UpdatedAtUtc);

public sealed record MonitoringPreservation(string ClipPublicId, bool IsPreserved, long Version);

public sealed class MonitoringRetentionUnavailableException()
    : Exception("monitoring_retention_unavailable");

public sealed class MonitoringRetentionRejectedException(string code) : Exception(code);

public interface IMonitoringBiometricCiphertextClient
{
    /// <summary>Returns only active encrypted payloads; the service never receives the key or plaintext.</summary>
    Task<IReadOnlyList<MonitoringBiometricCiphertext>> ListActiveAsync(
        CancellationToken cancellationToken = default);
}

public interface IMonitoringBiometricClient
{
    Task<IReadOnlyList<MonitoringBiometricEnrollment>> ListAsync(string userPublicId,
        CancellationToken cancellationToken = default);

    Task<MonitoringBiometricEnrollment> GetAsync(string publicId,
        CancellationToken cancellationToken = default);

    Task<MonitoringBiometricEnrollmentResult> EnrollAsync(MonitoringBiometricEnrollRequest request,
        CancellationToken cancellationToken = default);

    Task RevokeAsync(string publicId, string reason, CancellationToken cancellationToken = default);
}

public sealed record MonitoringBiometricEnrollRequest(string UserPublicId, byte[] EmbeddingCiphertext,
    byte[] EmbeddingNonce, byte[] EmbeddingTag, string KeyVersion, int Dimensions, string ModelId,
    string ModelVersion, string EmbeddingSchemaVersion, string AdapterId, string ConsentVersion);

public sealed record MonitoringBiometricEnrollmentResult(MonitoringBiometricEnrollment Enrollment,
    IReadOnlyList<string> ReplacedPublicIds);

public sealed record MonitoringBiometricEnrollment(string PublicId, string UserPublicId, string Status,
    DateTimeOffset EnrolledAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? RevokedAtUtc, string RevokedReason,
    string ModelId, string ModelVersion, int Dimensions, string KeyVersion, string ConsentVersion,
    DateTimeOffset ConsentAcceptedAtUtc);

public sealed record MonitoringBiometricCiphertext(string PublicId, byte[] EmbeddingCiphertext,
    byte[] EmbeddingNonce, byte[] EmbeddingTag, string KeyVersion, int Dimensions, string ModelId,
    string ModelVersion, string EmbeddingSchemaVersion, DateTimeOffset EnrolledAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed class MonitoringBiometricsUnavailableException()
    : Exception("monitoring_biometrics_unavailable");

public sealed class MonitoringBiometricsRejectedException(string code) : Exception(code);

public interface IMonitoringLiveClient
{
    Task<string?> GetPlaylistAsync(string cameraId, CancellationToken cancellationToken = default);

    Task<MonitoringLiveSegment?> GetSegmentAsync(string cameraId, string segmentId, string? range,
        CancellationToken cancellationToken = default);
}

public sealed record MonitoringLiveSegment(int StatusCode, string ContentType, byte[] Content,
    string? ContentRange);

public interface IMonitoringClipMediaClient
{
    Task<MonitoringClipMediaResult?> AssembleAsync(MonitoringClipMediaRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record MonitoringClipMediaRequest(string ClipId, string CameraId,
    IReadOnlyList<string> SegmentIds, DateTimeOffset RequestedStartedAt, DateTimeOffset RequestedEndedAt);

public sealed record MonitoringClipMediaResult(string MediaFileName, string MediaSha256,
    string ThumbnailFileName, string ThumbnailSha256);
