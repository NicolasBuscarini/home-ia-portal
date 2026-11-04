using System.Runtime.Versioning;
using GarageWatch.Infrastructure.Capture;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class FfmpegDiagnosticsTests
{
    [Fact]
    public void Sanitizes_rtsp_credentials_and_secret_parameters()
    {
        var line = "Error opening input rtsp://admin:secret@127.0.0.1:8554/garage/main?password=abc123: timed out";

        var sanitized = FfmpegDiagnostics.Sanitize(line);

        Assert.DoesNotContain("admin", sanitized);
        Assert.DoesNotContain("secret", sanitized);
        Assert.DoesNotContain("abc123", sanitized);
        Assert.Contains("<redacted>", sanitized);
        Assert.Contains("timed out", sanitized);
    }

    [Fact]
    public async Task Keeps_only_a_bounded_tail_of_stderr()
    {
        var diagnostics = new FfmpegDiagnostics(maxLines: 3);
        using var reader = new StringReader("um\ndois\ntres\nquatro\ncinco");

        await diagnostics.DrainAsync(reader);

        Assert.DoesNotContain("um |", diagnostics.Detail);
        Assert.DoesNotContain("dois", diagnostics.Detail);
        Assert.Contains("tres | quatro | cinco", diagnostics.Detail);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Adapter_reports_sanitized_ffmpeg_stderr_when_the_process_fails()
    {
        // /tmp is mounted noexec in the test container, so the fake FFmpeg lives on the workspace volume.
        var directory = Path.Combine(Directory.GetCurrentDirectory(), $"gw-ffmpeg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "ffmpeg-fake.sh");
        await File.WriteAllTextAsync(script,
            "#!/bin/sh\n" +
            "echo 'Error opening input rtsp://admin:secret@127.0.0.1:8554/garage/main: Connection timed out' >&2\n" +
            "exit 1\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var adapter = new FfmpegRtspCaptureAdapter(new CaptureOptions("ffmpeg-rtsp", "camera-1",
                Path.Combine(directory, "spool"), 1, 10, 1_048_576, "rtsp://127.0.0.1:8554/garage/main", script,
                "ffprobe"));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in adapter.CaptureAsync(Path.Combine(directory, "work"),
                                   CancellationToken.None)) { }
            });

            Assert.Equal("ffmpeg_stream_ended", exception.Message);
            var detail = Assert.IsType<string>(exception.Data["ffmpeg_stderr"]);
            Assert.Contains("Connection timed out", detail);
            Assert.DoesNotContain("admin", detail);
            Assert.DoesNotContain("secret", detail);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
