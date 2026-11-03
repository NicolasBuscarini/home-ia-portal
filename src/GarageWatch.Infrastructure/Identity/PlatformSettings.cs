using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Identity;

public sealed record PlatformSettingsValues(
    long EventJournalMaxEvents,
    long EventJournalMaxBytes,
    int ClipAnalysisQueueCapacity,
    int ClipAnalysisLeaseSeconds,
    int ClipArchivePollSeconds,
    int ClipArchiveMaxClips,
    long ClipArchiveMaxBytes,
    long ClipArchiveMaxClipBytes,
    long ClipPlaybackCacheBytes);

public sealed record PlatformSettingsDocument(
    int Version,
    DateTimeOffset UpdatedAtUtc,
    PlatformSettingsValues Values);

public sealed class PlatformSettingsConflictException(string message) : Exception(message);

public sealed class PlatformSettingsValidationException(string message) : Exception(message);

public static class PlatformSettingsFile
{
    private const string FileName = "platform-settings.json";
    private const long MiB = 1_048_576;
    private const long GiB = 1_073_741_824;
    private const long TiB = 1_099_511_627_776;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string ResolvePath(IConfiguration configuration)
    {
        var configured = configuration["PLATFORM_SETTINGS_FILE"];
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        var identityDbPath = configuration["IDENTITY_DB_PATH"];
        if (!string.IsNullOrWhiteSpace(identityDbPath))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(identityDbPath));
            if (!string.IsNullOrWhiteSpace(directory)) return Path.Combine(directory, FileName);
        }

        // Tests and local runs may not have IDENTITY_DB_PATH; keep the fallback writable and
        // process-scoped instead of failing on a container-only /data path.
        return Path.Combine(Path.GetTempPath(), "home-ia", $"platform-settings-{Environment.ProcessId}.json");
    }

    public static PlatformSettingsDocument LoadOrCreate(IConfiguration configuration)
    {
        lock (Sync)
        {
            var path = ResolvePath(configuration);
            if (File.Exists(path))
            {
                var document = JsonSerializer.Deserialize<PlatformSettingsDocument>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidOperationException($"Arquivo de configurações inválido: {path}");
                Validate(document.Values);
                return document with { Version = Math.Max(1, document.Version) };
            }

            var values = FromConfiguration(configuration);
            Validate(values);
            return Write(path, 1, values);
        }
    }

    public static PlatformSettingsDocument Save(
        IConfiguration configuration,
        int expectedVersion,
        PlatformSettingsValues values)
    {
        lock (Sync)
        {
            var path = ResolvePath(configuration);
            var current = LoadOrCreate(configuration);
            if (current.Version != expectedVersion)
            {
                throw new PlatformSettingsConflictException(
                    $"Versão esperada {expectedVersion}, versão atual {current.Version}.");
            }

            Validate(values);
            return Write(path, current.Version + 1, values);
        }
    }

    public static IReadOnlyDictionary<string, string?> ToConfiguration(PlatformSettingsValues values) =>
        new Dictionary<string, string?>
        {
            ["EVENT_JOURNAL_MAX_EVENTS"] = values.EventJournalMaxEvents.ToString(CultureInfo.InvariantCulture),
            ["EVENT_JOURNAL_MAX_BYTES"] = values.EventJournalMaxBytes.ToString(CultureInfo.InvariantCulture),
            ["CLIP_ANALYSIS_QUEUE_CAPACITY"] = values.ClipAnalysisQueueCapacity.ToString(CultureInfo.InvariantCulture),
            ["CLIP_ANALYSIS_LEASE_SECONDS"] = values.ClipAnalysisLeaseSeconds.ToString(CultureInfo.InvariantCulture),
            ["CLIP_ARCHIVE_POLL_SECONDS"] = values.ClipArchivePollSeconds.ToString(CultureInfo.InvariantCulture),
            ["CLIP_ARCHIVE_MAX_CLIPS"] = values.ClipArchiveMaxClips.ToString(CultureInfo.InvariantCulture),
            ["CLIP_ARCHIVE_MAX_BYTES"] = values.ClipArchiveMaxBytes.ToString(CultureInfo.InvariantCulture),
            ["CLIP_ARCHIVE_MAX_CLIP_BYTES"] = values.ClipArchiveMaxClipBytes.ToString(CultureInfo.InvariantCulture),
            ["CLIP_PLAYBACK_CACHE_BYTES"] = values.ClipPlaybackCacheBytes.ToString(CultureInfo.InvariantCulture),
        };

    public static PlatformSettingsValues FromConfiguration(IConfiguration configuration) =>
        new(
            ReadLong(configuration, "EVENT_JOURNAL_MAX_EVENTS", 10_000),
            ReadLong(configuration, "EVENT_JOURNAL_MAX_BYTES", 67_108_864),
            (int)ReadLong(configuration, "CLIP_ANALYSIS_QUEUE_CAPACITY", 8),
            (int)ReadLong(configuration, "CLIP_ANALYSIS_LEASE_SECONDS", 60),
            (int)ReadLong(configuration, "CLIP_ARCHIVE_POLL_SECONDS", 2),
            (int)ReadLong(configuration, "CLIP_ARCHIVE_MAX_CLIPS", 10_000),
            ReadLong(configuration, "CLIP_ARCHIVE_MAX_BYTES", 53_687_091_200),
            ReadLong(configuration, "CLIP_ARCHIVE_MAX_CLIP_BYTES", 1_073_741_824),
            ReadLong(configuration, "CLIP_PLAYBACK_CACHE_BYTES", 2_147_483_648));

    public static void Validate(PlatformSettingsValues values)
    {
        RequireRange(values.EventJournalMaxEvents, 1, 100_000, "EVENT_JOURNAL_MAX_EVENTS");
        RequireRange(values.EventJournalMaxBytes, MiB, GiB, "EVENT_JOURNAL_MAX_BYTES");
        RequireRange(values.ClipAnalysisQueueCapacity, 1, 64, "CLIP_ANALYSIS_QUEUE_CAPACITY");
        RequireRange(values.ClipAnalysisLeaseSeconds, 30, 300, "CLIP_ANALYSIS_LEASE_SECONDS");
        RequireRange(values.ClipArchivePollSeconds, 1, 60, "CLIP_ARCHIVE_POLL_SECONDS");
        RequireRange(values.ClipArchiveMaxClips, 1, 100_000, "CLIP_ARCHIVE_MAX_CLIPS");
        RequireRange(values.ClipArchiveMaxBytes, MiB, TiB, "CLIP_ARCHIVE_MAX_BYTES");
        RequireRange(values.ClipArchiveMaxClipBytes, MiB, 10 * GiB, "CLIP_ARCHIVE_MAX_CLIP_BYTES");
        RequireRange(values.ClipPlaybackCacheBytes, 64 * MiB, 64 * GiB, "CLIP_PLAYBACK_CACHE_BYTES");
    }

    private static PlatformSettingsDocument Write(string path, int version, PlatformSettingsValues values)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var document = new PlatformSettingsDocument(version, DateTimeOffset.UtcNow, values);
        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(temporary, path, true);
        return document;
    }

    private static long ReadLong(IConfiguration configuration, string key, long fallback)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new PlatformSettingsValidationException($"{key} deve ser um número inteiro.");
        }
        return value;
    }

    private static void RequireRange(long value, long minimum, long maximum, string key)
    {
        if (value < minimum || value > maximum)
        {
            throw new PlatformSettingsValidationException(
                $"{key} deve estar entre {minimum.ToString(CultureInfo.InvariantCulture)} e {maximum.ToString(CultureInfo.InvariantCulture)}.");
        }
    }
}
