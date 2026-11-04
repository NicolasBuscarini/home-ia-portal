using System.Text.RegularExpressions;

namespace GarageWatch.Infrastructure.Capture;

/// <summary>
/// Keeps a small, sanitized tail of the FFmpeg error stream so a failed capture attempt can be
/// diagnosed without persisting the RTSP URL or credentials. FFmpeg echoes the full input URL in
/// some errors, so every line is redacted before it is retained.
/// </summary>
public sealed partial class FfmpegDiagnostics(int maxLines = 12)
{
    private readonly Queue<string> _lines = new();

    public string Detail => string.Join(" | ", _lines);

    public async Task DrainAsync(TextReader reader, CancellationToken cancellationToken = default)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var sanitized = Sanitize(line);
            if (sanitized.Length == 0) continue;
            _lines.Enqueue(sanitized.Length > 300 ? sanitized[..300] : sanitized);
            while (_lines.Count > maxLines) _lines.Dequeue();
        }
    }

    public static string Sanitize(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return string.Empty;
        var sanitized = RtspUrlPattern().Replace(line.Trim(), "rtsp://<redacted>");
        sanitized = SecretQueryPattern().Replace(sanitized, "$1<redacted>");
        return SensitiveTokenPattern().Replace(sanitized, "$1<redacted>");
    }

    [GeneratedRegex(@"rtsp://[^\s'""]+", RegexOptions.IgnoreCase)]
    private static partial Regex RtspUrlPattern();

    [GeneratedRegex(@"([?&](?:password|pass|token|secret)=)[^&\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretQueryPattern();

    [GeneratedRegex(@"((?:password|token|secret)\s*[:=]\s*)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveTokenPattern();
}
