using GarageWatch.Application.Monitoring;

namespace GarageWatch.Infrastructure.Capture;

/// <summary>
/// Delegates clip media and thumbnail materialization to the monitoring service while the portal keeps
/// the clip manifest state machine, trigger policy and authorization.
/// </summary>
public sealed class RemoteClipMediaAssembler(IMonitoringClipMediaClient client) : IClipMediaAssembler
{
    public async Task<(string MediaFileName, string MediaSha256, string ThumbnailFileName, string ThumbnailSha256)>
        AssembleAsync(ClipManifestV1 clip, IReadOnlyList<string> segmentPaths, CancellationToken cancellationToken)
    {
        var result = await client.AssembleAsync(new MonitoringClipMediaRequest(clip.ClipId, clip.CameraId,
            clip.SegmentIds, clip.RequestedStartedAt, clip.RequestedEndedAt), cancellationToken);
        if (result is null) throw new IOException("monitoring_clip_media_unavailable");
        return (result.MediaFileName, result.MediaSha256, result.ThumbnailFileName, result.ThumbnailSha256);
    }
}
