namespace GarageWatch.Application.Monitoring;

/// <summary>Normalized face box (0..1 of the frame) detected in a sampled frame.</summary>
public sealed record MonitoringClipFrameFace(double X, double Y, double Width, double Height);

/// <summary>One deterministic frame position sampled by the face observation source for a clip.</summary>
public sealed record MonitoringClipFrame(int Index, long OffsetMilliseconds,
    IReadOnlyList<MonitoringClipFrameFace>? Faces = null);

/// <summary>
/// Positions recomputed by the monitoring service from the clip duration. Only offsets cross the
/// boundary here: no frame is persisted and the identity contract keeps carrying no image.
/// </summary>
public sealed record MonitoringClipFrameManifest(string ClipPublicId, long DurationMilliseconds,
    int SampledFrames, IReadOnlyList<MonitoringClipFrame> Frames);

/// <summary>JPEG bytes of one sampled frame, rendered on demand from the validated playback file.</summary>
public sealed record MonitoringClipFrameImage(byte[] Content, string ContentType);

public interface IMonitoringClipFramesClient
{
    Task<MonitoringClipFrameManifest?> GetManifestAsync(string clipPublicId,
        CancellationToken cancellationToken = default);

    Task<MonitoringClipFrameImage?> GetFrameAsync(string clipPublicId, int index,
        CancellationToken cancellationToken = default);
}
