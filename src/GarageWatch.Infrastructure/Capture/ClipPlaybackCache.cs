using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Capture;

/// <summary>
/// Bounded cache of browser-playable copies. The authoritative clip stays an MPEG-TS concatenation
/// (contract and archive unchanged); this cache holds a remuxed MP4 per played clip, preserving the
/// AAC audio track when the source has one.
/// </summary>
public sealed record ClipPlaybackOptions(string Directory, long MaximumBytes, int TimeoutSeconds)
{
    public const long DefaultMaximumBytes = 2L * 1024 * 1024 * 1024;
    public const int DefaultTimeoutSeconds = 30;

    public static ClipPlaybackOptions FromEnvironment(IConfiguration configuration, CaptureOptions capture)
    {
        var directory = Path.Combine(capture.SpoolDirectory, "clips", "playback");
        var maximumBytes = DefaultMaximumBytes;
        var raw = configuration["CLIP_PLAYBACK_CACHE_BYTES"];
        if (!string.IsNullOrWhiteSpace(raw) &&
            (!long.TryParse(raw, out maximumBytes) || maximumBytes is < 67_108_864 or > 68_719_476_736))
            throw new InvalidOperationException("CLIP_PLAYBACK_CACHE_BYTES deve estar entre 64 MiB e 64 GiB.");
        return new ClipPlaybackOptions(directory, maximumBytes, DefaultTimeoutSeconds);
    }
}

/// <summary>
/// Remuxes a stored clip into a fragmented-free MP4 (H.264/AAC copy, faststart) so browsers can play
/// it, keeping audio when present. Derived files are keyed by the source hash, written atomically and
/// pruned by total size.
/// </summary>
public sealed class ClipPlaybackCache(ClipPlaybackOptions options, CaptureOptions capture,
    ILogger<ClipPlaybackCache> logger)
{
    private readonly SemaphoreSlim conversionGate = new(1, 1);

    public string? ResolveExisting(string clipId, string mediaSha256, bool includeAudio)
    {
        var path = PathFor(clipId, mediaSha256, includeAudio);
        return path is not null && File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    public async Task<string?> EnsureAsync(string clipId, string sourcePath, string mediaSha256,
        bool includeAudio, CancellationToken cancellationToken)
    {
        if (PathFor(clipId, mediaSha256, includeAudio) is not { } destination) return null;
        var existing = ResolveExisting(clipId, mediaSha256, includeAudio);
        if (existing is not null) return existing;
        await conversionGate.WaitAsync(cancellationToken);
        try
        {
            existing = ResolveExisting(clipId, mediaSha256, includeAudio);
            if (existing is not null) return existing;
            Directory.CreateDirectory(options.Directory);
            var temporary = Path.Combine(options.Directory,
                $"{Path.GetFileNameWithoutExtension(destination)}-{Guid.NewGuid():N}.tmp.mp4");
            try
            {
                if (!await RemuxAsync(clipId, sourcePath, temporary, includeAudio, cancellationToken)) return null;
                if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                {
                    logger.LogWarning("Cópia de reprodução do clipe vazia; mídia original preservada. clip={ClipId}",
                        clipId);
                    return null;
                }
                File.Move(temporary, destination, overwrite: true);
                TryDeleteLegacyCopy(clipId, mediaSha256);
                Prune(destination);
                return destination;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally
        {
            conversionGate.Release();
        }
    }

    private string? PathFor(string clipId, string mediaSha256, bool includeAudio)
    {
        if (!LocalSegmentSpool.IsSafeSegmentId(clipId)) return null;
        if (mediaSha256 is not { Length: 64 } || !mediaSha256.All(Uri.IsHexDigit)) return null;
        var variant = includeAudio ? "a1" : "s1";
        return Path.Combine(options.Directory, $"{clipId}-{mediaSha256[..12]}-{variant}.mp4");
    }

    private async Task<bool> RemuxAsync(string clipId, string sourcePath, string destination, bool includeAudio,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(capture.FfmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        var arguments = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", sourcePath,
            "-map", "0:v:0"
        };
        if (includeAudio) arguments.AddRange(["-map", "0:a:0?"]);
        else arguments.Add("-an");
        arguments.AddRange(["-c", "copy", "-movflags", "+faststart", "-f", "mp4", destination]);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null) return false;
            var stderr = await process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode == 0) return true;
            logger.LogWarning("Remux de reprodução do clipe falhou; mídia original preservada. clip={ClipId} exit={ExitCode}",
                clipId, process.ExitCode);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            logger.LogWarning("Remux de reprodução do clipe não concluiu no tempo limite. clip={ClipId}", clipId);
            return false;
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            process?.Dispose();
        }
    }

    /// <summary>
    /// Copies produced before audio support have no suffix; delete the one for this clip so a
    /// previously silent playback is regenerated instead of reused.
    /// </summary>
    private void TryDeleteLegacyCopy(string clipId, string mediaSha256)
    {
        var legacy = Path.Combine(options.Directory, $"{clipId}-{mediaSha256[..12]}.mp4");
        try
        {
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Prune(string keepPath)
    {
        var files = new DirectoryInfo(options.Directory).EnumerateFiles("*.mp4")
            .Where(file => file.FullName != Path.GetFullPath(keepPath))
            .OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        var total = files.Sum(file => file.Length) + new FileInfo(keepPath).Length;
        foreach (var file in files)
        {
            if (total <= options.MaximumBytes) break;
            try
            {
                var length = file.Length;
                file.Delete();
                total -= length;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
