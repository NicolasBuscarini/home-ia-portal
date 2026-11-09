using System.Security.Cryptography;
using System.Text.Json;
using GarageWatch.Application.Maintenance;
using GarageWatch.Infrastructure.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Maintenance;

/// <summary>
/// Creates consistent copies of the portal SQLite database with <c>VACUUM INTO</c>, so a running
/// service is not copied page by page while WAL pages are pending. Each artifact is verified with
/// <c>PRAGMA integrity_check</c> and described by a JSON manifest carrying size and SHA-256; an
/// operator copies the directory to independent storage (NAS/host) and restores with services stopped.
/// </summary>
public sealed class PortalBackupService : IPortalBackupService
{
    public const string BackupDirectoryKey = "PORTAL_BACKUP_DIRECTORY";

    private const string ArtifactPrefix = "portal-";
    private const string ManifestSuffix = ".db.json";
    private const int ManifestSchemaVersion = 1;
    private const int MaximumListedArtifacts = 50;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string databasePath;
    private readonly string backupDirectory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<PortalBackupService> logger;

    public PortalBackupService(IConfiguration configuration, TimeProvider timeProvider,
        ILogger<PortalBackupService> logger)
    {
        this.timeProvider = timeProvider;
        this.logger = logger;
        databasePath = configuration[IdentityBootstrapOptions.DatabasePathKey]
            ?? IdentityBootstrapOptions.DefaultDatabasePath;
        if (!Path.IsPathFullyQualified(databasePath))
            throw new InvalidOperationException("IDENTITY_DB_PATH deve ser um caminho absoluto.");
        var configured = configuration[BackupDirectoryKey];
        backupDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetDirectoryName(databasePath)!, "backups")
            : configured;
        if (!Path.IsPathFullyQualified(backupDirectory))
            throw new InvalidOperationException($"{BackupDirectoryKey} deve ser um caminho absoluto.");
    }

    public async Task<PortalBackupArtifact> CreateAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(backupDirectory);
        var createdAtUtc = timeProvider.GetUtcNow();
        var target = ResolveArtifactPath(createdAtUtc);
        try
        {
            await CopyAsync(target, cancellationToken);
            await VerifyAsync(target, cancellationToken);
            var size = new FileInfo(target).Length;
            var sha256 = await HashAsync(target, cancellationToken);
            var artifact = new PortalBackupArtifact(Path.GetFileName(target), createdAtUtc, size, sha256);
            await WriteManifestAsync(artifact, cancellationToken);
            logger.LogInformation("Backup do portal criado: {FileName} ({SizeBytes} bytes, sha256 {Sha256}).",
                artifact.FileName, artifact.SizeBytes, artifact.Sha256);
            return artifact;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    public async Task<IReadOnlyList<PortalBackupArtifact>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(backupDirectory)) return [];
        var artifacts = new List<PortalBackupArtifact>();
        foreach (var manifestPath in Directory.EnumerateFiles(backupDirectory, $"{ArtifactPrefix}*{ManifestSuffix}"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(manifestPath);
                var manifest = await JsonSerializer.DeserializeAsync<Manifest>(stream, SerializerOptions,
                    cancellationToken);
                if (manifest?.Artifact is null) continue;
                if (!File.Exists(Path.Combine(backupDirectory, manifest.Artifact.FileName)))
                {
                    logger.LogWarning("Manifesto de backup sem artefato: {FileName}", manifest.Artifact.FileName);
                    continue;
                }
                artifacts.Add(new PortalBackupArtifact(manifest.Artifact.FileName, manifest.Artifact.CreatedAtUtc,
                    manifest.Artifact.SizeBytes, manifest.Artifact.Sha256));
            }
            catch (JsonException)
            {
                logger.LogWarning("Manifesto de backup ilegível ignorado: {FileName}", Path.GetFileName(manifestPath));
            }
            catch (IOException)
            {
                logger.LogWarning("Manifesto de backup inacessível ignorado: {FileName}", Path.GetFileName(manifestPath));
            }
        }
        return artifacts
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.FileName, StringComparer.Ordinal)
            .Take(MaximumListedArtifacts)
            .ToArray();
    }

    private string ResolveArtifactPath(DateTimeOffset createdAtUtc)
    {
        var stamp = createdAtUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
        var candidate = Path.Combine(backupDirectory, $"{ArtifactPrefix}{stamp}.db");
        for (var suffix = 1; File.Exists(candidate); suffix++)
        {
            if (suffix > 20)
                throw new InvalidOperationException("Não foi possível alocar um nome único para o backup do portal.");
            candidate = Path.Combine(backupDirectory, $"{ArtifactPrefix}{stamp}-{suffix}.db");
        }
        return candidate;
    }

    private async Task CopyAsync(string target, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $target";
        command.Parameters.AddWithValue("$target", target);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task VerifyAsync(string artifactPath, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = artifactPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        var result = (await command.ExecuteScalarAsync(cancellationToken))?.ToString();
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O backup do portal não passou na verificação de integridade.");
    }

    private static async Task<string> HashAsync(string artifactPath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(artifactPath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task WriteManifestAsync(PortalBackupArtifact artifact, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(backupDirectory, artifact.FileName + ".json");
        var temporaryPath = manifestPath + ".tmp";
        var manifest = new Manifest(ManifestSchemaVersion, artifact, Path.GetFileName(databasePath));
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, manifest, SerializerOptions, cancellationToken);
        File.Move(temporaryPath, manifestPath, true);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException exception)
        {
            logger.LogWarning("Não foi possível remover artefato incompleto de backup: {ExceptionType}",
                exception.GetType().Name);
        }
    }

    private sealed record Manifest(int Schema, PortalBackupArtifact Artifact, string SourceDatabase);
}
