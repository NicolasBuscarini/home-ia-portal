using System.Security.Cryptography;
using System.Text.Json;

namespace GarageWatch.Infrastructure.Capture;

public sealed record ClipManifestV1(
    string SchemaVersion,
    string ClipId,
    string CameraId,
    DateTimeOffset RequestedStartedAt,
    DateTimeOffset RequestedEndedAt,
    DateTimeOffset EffectiveStartedAt,
    DateTimeOffset EffectiveEndedAt,
    string State,
    string TriggerConfigVersion,
    string TriggerReason,
    IReadOnlyList<string> SegmentIds,
    string? MediaFileName,
    string? MediaSha256,
    string? ThumbnailFileName,
    string? ThumbnailSha256,
    int AttemptCount,
    DateTimeOffset UpdatedAt,
    string ArchiveStatus = "not_requested",
    int ArchiveAttemptCount = 0,
    DateTimeOffset? ArchiveNextAttemptAt = null,
    DateTimeOffset? ArchivedAt = null,
    string? ArchiveManifestSha256 = null,
    string? PublicId = null);

public interface IClipMediaAssembler
{
    Task<(string MediaFileName, string MediaSha256, string ThumbnailFileName, string ThumbnailSha256)> AssembleAsync(
        ClipManifestV1 clip, IReadOnlyList<string> segmentPaths, CancellationToken cancellationToken);
}

public interface IVisualChangeDetector
{
    Task<bool> HasRelevantChangeAsync(string segmentPath, CancellationToken cancellationToken);
}

public sealed class FfmpegVisualChangeDetector(CaptureOptions capture, ClipTriggerOptions options) : IVisualChangeDetector
{
    private byte[]? previous;

    public async Task<bool> HasRelevantChangeAsync(string segmentPath, CancellationToken cancellationToken)
    {
        var start = new System.Diagnostics.ProcessStartInfo(capture.FfmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-i", segmentPath,
                     "-vf", $"fps={options.SampleFps},scale={options.Width}:{options.Height}", "-frames:v", "1",
                     "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1" }) start.ArgumentList.Add(argument);
        using var process = new System.Diagnostics.Process { StartInfo = start };
        if (!process.Start()) return false;
        await using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(copy, errors);
        var frame = output.ToArray();
        if (process.ExitCode != 0 || frame.Length != options.Width * options.Height) return false;
        var old = Interlocked.Exchange(ref previous, frame);
        if (old is null || old.Length != frame.Length) return false;
        var changedEverywhere = 0;
        var changedInScope = 0;
        var scopedPixels = 0;
        for (var index = 0; index < frame.Length; index++)
        {
            var changed = Math.Abs(frame[index] - old[index]) >= options.PixelDelta;
            if (changed) changedEverywhere++;
            var x = (index % options.Width + .5) / options.Width;
            var y = (index / options.Width + .5) / options.Height;
            if (!(options.Zone ?? new NormalizedRectangle(0, 0, 1, 1)).Contains(x, y)
                || (options.Masks ?? []).Any(mask => mask.Contains(x, y))) continue;
            scopedPixels++;
            if (changed) changedInScope++;
        }
        if (changedEverywhere / (double)frame.Length >= options.GlobalChangedAreaRatio) return false;
        return scopedPixels > 0 && changedInScope / (double)scopedPixels >= options.ChangedAreaRatio;
    }
}
