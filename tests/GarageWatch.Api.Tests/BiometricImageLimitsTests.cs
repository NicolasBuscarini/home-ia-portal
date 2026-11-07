using System.Diagnostics;
using GarageWatch.Application.Analysis;
using GarageWatch.Infrastructure.Biometrics;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class BiometricImageLimitsTests
{
    [Fact]
    public void Upload_contract_accepts_large_payloads_but_bounds_dimensions()
    {
        Assert.True(new FaceCropV1("image/jpeg", 6000, 6000, new byte[10]).IsValid);
        Assert.True(new FaceCropV1("image/jpeg", 4000, 3000, new byte[12 * 1024 * 1024]).IsValid);
        Assert.False(new FaceCropV1("image/jpeg", 6001, 3000, new byte[10]).IsValid);
        Assert.False(new FaceCropV1("image/jpeg", 3000, 6001, new byte[10]).IsValid);
        Assert.False(new FaceCropV1("image/jpeg", 4000, 3000, new byte[12 * 1024 * 1024 + 1]).IsValid);
        Assert.False(new FaceCropV1("image/gif", 160, 160, new byte[10]).IsValid);
    }

    [Fact]
    public async Task Oversized_image_is_downscaled_before_inference()
    {
        var directory = Directory.CreateTempSubdirectory("garagewatch-image-");
        try
        {
            var path = Path.Combine(directory.FullName, "large.jpg");
            RunFfmpeg(path, "3000x2000");

            var image = await FaceImageDecoder.DecodeAsync("ffmpeg", await File.ReadAllBytesAsync(path),
                20, CancellationToken.None, maximumDimension: 1600);

            Assert.NotNull(image);
            Assert.True(image!.IsValid);
            Assert.True(image.Width <= 1600 && image.Height <= 1600);
            Assert.Equal(1600, image.Width);
            Assert.InRange(Math.Abs((double)image.Width / image.Height - 1.5), 0, 0.01);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void RunFfmpeg(string destination, string size)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                     $"color=c=gray:s={size}", "-frames:v", "1", "-y", destination
                 })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg_start_failed");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
    }
}
