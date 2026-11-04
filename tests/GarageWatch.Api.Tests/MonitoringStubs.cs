using GarageWatch.Application.Monitoring;

namespace GarageWatch.Api.Tests;

public sealed class CapturingModuleClient : IMonitoringModuleClient
{
    public Task<MonitoringModuleContract> GetContractAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new MonitoringModuleContract("1.0", "monitoring", "1.0", "remote",
            ["remote-capture", "clips.read", "live.hls.read"], DateTimeOffset.UtcNow, true, null));

    public Task<MonitoringCameraStatus> GetCameraStatusAsync(string cameraId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new MonitoringCameraStatus("1.0", "monitoring", cameraId, "capturing",
            "epoch-stub", 5, 0, DateTimeOffset.UtcNow, null, true, null));
}

public sealed class UnreachableModuleClient : IMonitoringModuleClient
{
    public Task<MonitoringModuleContract> GetContractAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new MonitoringModuleContract("1.0", "monitoring", "1.0", "remote", [], null, false,
            "monitoring_unreachable"));

    public Task<MonitoringCameraStatus> GetCameraStatusAsync(string cameraId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new MonitoringCameraStatus("1.0", "monitoring", cameraId, "unreachable", null, 0, 0,
            null, "monitoring_unreachable", false, "monitoring_unreachable"));
}

public sealed class SlidingPlaylistLiveClient : IMonitoringLiveClient
{
    private static readonly byte[] Payload = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    private int _sequence;

    public Task<string?> GetPlaylistAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var segmentId = sequence.ToString("x32");
        return Task.FromResult<string?>(
            $"#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:5\n#EXT-X-MEDIA-SEQUENCE:{sequence}\n" +
            $"#EXTINF:5.0,\n/api/v1/live/{cameraId}/segments/{segmentId}\n");
    }

    public Task<MonitoringLiveSegment?> GetSegmentAsync(string cameraId, string segmentId, string? range,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<MonitoringLiveSegment?>(string.IsNullOrEmpty(range)
            ? new MonitoringLiveSegment(200, "video/mp2t", Payload, null)
            : new MonitoringLiveSegment(206, "video/mp2t", Payload[..10], "bytes 0-9/32"));
}
