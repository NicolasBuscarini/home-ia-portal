namespace GarageWatch.Infrastructure.Capture;

public sealed record ArchivedClipManifestV1(string SchemaVersion, string ClipId, string PublicId, string CameraId,
    DateTimeOffset RequestedStartedAt, DateTimeOffset RequestedEndedAt, DateTimeOffset EffectiveStartedAt,
    DateTimeOffset EffectiveEndedAt, string TriggerConfigVersion, string TriggerReason,
    IReadOnlyList<string> SegmentIds, string MediaFileName, long MediaSizeBytes, string MediaSha256,
    string ThumbnailFileName, long ThumbnailSizeBytes, string ThumbnailSha256, DateTimeOffset ArchivedAt);

public sealed record ClipArchiveReceiptV1(string ManifestFileName, string ManifestSha256,
    string MediaFileName, string MediaSha256, string ThumbnailFileName, string ThumbnailSha256,
    DateTimeOffset ArchivedAt);
