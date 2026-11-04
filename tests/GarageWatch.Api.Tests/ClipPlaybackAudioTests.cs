using System.Diagnostics;
using System.Runtime.Versioning;
using GarageWatch.Infrastructure.Capture;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GarageWatch.Api.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class ClipPlaybackAudioTests
{
    [Fact]
    public async Task Remux_preserves_the_aac_audio_track_when_the_clip_has_one()
    {
        await using var fixture = new PlaybackFixture();
        var source = await fixture.CreateSourceAsync(withAudio: true);
        var legacy = fixture.CreateLegacyCopy(new string('a', 64));

        var output = await fixture.Cache.EnsureAsync(fixture.ClipId, source, new string('a', 64), includeAudio: true,
            CancellationToken.None);

        Assert.NotNull(output);
        Assert.False(File.Exists(legacy));
        var streams = await fixture.ProbeStreamsAsync(output!);
        Assert.Contains("video", streams);
        Assert.Contains("audio", streams);
    }

    [Fact]
    public async Task Remux_produces_a_silent_copy_when_audio_is_not_authorized()
    {
        await using var fixture = new PlaybackFixture();
        var source = await fixture.CreateSourceAsync(withAudio: true);

        var output = await fixture.Cache.EnsureAsync(fixture.ClipId, source, new string('b', 64), includeAudio: false,
            CancellationToken.None);

        Assert.NotNull(output);
        var streams = await fixture.ProbeStreamsAsync(output!);
        Assert.Contains("video", streams);
        Assert.DoesNotContain("audio", streams);
    }

    private sealed class PlaybackFixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Directory.GetCurrentDirectory(),
            $"gw-clip-audio-{Guid.NewGuid():N}");

        public PlaybackFixture()
        {
            Directory.CreateDirectory(directory);
            var capture = new CaptureOptions("ffmpeg-rtsp", "camera-1", directory, 1, 10, 1_048_576,
                "rtsp://127.0.0.1:8554/garage/main", "ffmpeg", "ffprobe", AudioEnabled: true);
            Cache = new ClipPlaybackCache(
                new ClipPlaybackOptions(Path.Combine(directory, "playback"), 67_108_864, 30), capture,
                NullLogger<ClipPlaybackCache>.Instance);
        }

        public string ClipId { get; } = "0123456789abcdef0123456789abcdef";

        public ClipPlaybackCache Cache { get; }

        public string CreateLegacyCopy(string mediaSha256)
        {
            var directory = Path.Combine(this.directory, "playback");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{ClipId}-{mediaSha256[..12]}.mp4");
            File.WriteAllBytes(path, [0x00]);
            return path;
        }

        public async Task<string> CreateSourceAsync(bool withAudio)
        {
            var source = Path.Combine(directory, withAudio ? "source-audio.ts" : "source-silent.ts");
            var arguments = new List<string>
            {
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=10"
            };
            if (withAudio) arguments.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=16000"]);
            arguments.AddRange(["-t", "1", "-c:v", "libx264", "-preset", "ultrafast"]);
            if (withAudio) arguments.AddRange(["-c:a", "aac"]);
            arguments.AddRange(["-f", "mpegts", source]);
            await RunAsync("ffmpeg", arguments);
            return source;
        }

        public async Task<string[]> ProbeStreamsAsync(string path)
        {
            var output = await RunAsync("ffprobe",
                ["-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", path]);
            return output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim()).ToArray();
        }

        private static async Task<string> RunAsync(string fileName, IReadOnlyList<string> arguments)
        {
            var start = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName}_start_failed");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"{fileName} falhou: {stderr}");
            return stdout;
        }

        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            return ValueTask.CompletedTask;
        }
    }
}
