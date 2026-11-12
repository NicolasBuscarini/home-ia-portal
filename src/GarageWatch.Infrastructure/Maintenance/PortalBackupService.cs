using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using GarageWatch.Application.Maintenance;
using GarageWatch.Infrastructure.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Maintenance;

/// <summary>
/// Creates consistent, encrypted copies of the portal SQLite database with <c>VACUUM INTO</c>, so a
/// running service is not copied page by page while WAL pages are pending. Each artifact is verified
/// with <c>PRAGMA integrity_check</c>, described by a JSON manifest carrying the plaintext size/SHA-256
/// and the ciphertext SHA-256 plus key version, and sealed with AES-256-GCM so the backup directory
/// never holds the database in clear. An operator copies the directory to independent storage
/// (NAS/host) and restores with services stopped. <see cref="RestoreAsync"/> re-verifies the manifest
/// hash and integrity and restores the artifact into an isolated directory only, proving it opens as a
/// working database without touching the live path.
/// </summary>
public sealed class PortalBackupService : IPortalBackupService
{
    public const string BackupDirectoryKey = "PORTAL_BACKUP_DIRECTORY";

    private const string ArtifactPrefix = "portal-";
    private const string ManifestSuffix = ".db.json";
    private const int ManifestSchemaVersion = 2;
    private const int MaximumListedArtifacts = 50;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string databasePath;
    private readonly string backupDirectory;
    private readonly TimeProvider timeProvider;
    private readonly IBackupArtifactProtector protector;
    private readonly PortalBackupMetrics metrics;
    private readonly ILogger<PortalBackupService> logger;

    public PortalBackupService(IConfiguration configuration, TimeProvider timeProvider,
        IBackupArtifactProtector protector, PortalBackupMetrics metrics, ILogger<PortalBackupService> logger)
    {
        this.timeProvider = timeProvider;
        this.protector = protector;
        this.metrics = metrics;
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
        if (!protector.IsConfigured)
        {
            metrics.Failed(timeProvider.GetUtcNow());
            throw new InvalidOperationException(
                "Cifra de backup indisponível: configure PORTAL_BACKUP_ENCRYPTION_KEY e PORTAL_BACKUP_ENCRYPTION_KEY_VERSION.");
        }
        Directory.CreateDirectory(backupDirectory);
        var createdAtUtc = timeProvider.GetUtcNow();
        var target = ResolveArtifactPath(createdAtUtc);
        var plaintextPath = target + ".plain";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await CopyAsync(plaintextPath, cancellationToken);
            await VerifyAsync(plaintextPath, cancellationToken);
            var size = new FileInfo(plaintextPath).Length;
            var sha256 = await HashFileAsync(plaintextPath, cancellationToken);

            var plaintext = await File.ReadAllBytesAsync(plaintextPath, cancellationToken);
            var envelope = protector.Seal(plaintext);
            await WriteEnvelopeAsync(target, envelope, cancellationToken);
            var cipherSha256 = await HashFileAsync(target, cancellationToken);

            var artifact = new PortalBackupArtifact(Path.GetFileName(target), createdAtUtc, size, sha256);
            await WriteManifestAsync(artifact, cipherSha256, cancellationToken);
            metrics.Created(size, createdAtUtc);
            metrics.ObservedDuration(stopwatch.Elapsed);
            logger.LogInformation(
                "Backup do portal criado (cifrado, chave {KeyVersion}): {FileName} ({SizeBytes} bytes, sha256 {Sha256}).",
                protector.ActiveKeyVersion, artifact.FileName, artifact.SizeBytes, artifact.Sha256);
            return artifact;
        }
        catch
        {
            metrics.Failed(timeProvider.GetUtcNow());
            TryDelete(target);
            throw;
        }
        finally
        {
            // The plaintext working copy must never survive, on success or failure.
            TryDelete(plaintextPath);
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

    public async Task<PortalRestoreResult> RestoreAsync(string backupFileName, string targetDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        if (Path.GetFileName(backupFileName) != backupFileName)
            throw new InvalidOperationException("O nome do artefato de backup deve ser um nome de arquivo, sem diretórios.");
        if (!Path.IsPathFullyQualified(targetDirectory))
            throw new InvalidOperationException("O diretório de destino do restore deve ser um caminho absoluto.");

        var manifest = await ReadManifestAsync(backupFileName, cancellationToken)
            ?? throw new InvalidOperationException($"Manifesto de backup ausente ou ilegível: {backupFileName}.");
        var artifactPath = Path.Combine(backupDirectory, manifest.Artifact.FileName);
        if (!File.Exists(artifactPath))
            throw new InvalidOperationException($"Artefato de backup ausente: {manifest.Artifact.FileName}.");

        var artifactBytes = await File.ReadAllBytesAsync(artifactPath, cancellationToken);
        var plaintext = manifest.Encryption is null
            ? artifactBytes
            : DecryptArtifact(artifactBytes, manifest.Encryption);

        var plaintextSha256 = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();
        if (!string.Equals(plaintextSha256, manifest.Artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O conteúdo do artefato não confere com o SHA-256 registrado no manifesto.");

        Directory.CreateDirectory(targetDirectory);
        var restoredPath = Path.Combine(targetDirectory, manifest.Artifact.FileName);
        if (File.Exists(restoredPath))
            throw new InvalidOperationException($"O destino do restore já existe: {restoredPath}.");

        await File.WriteAllBytesAsync(restoredPath, plaintext, cancellationToken);
        await VerifyAsync(restoredPath, cancellationToken);

        var tableCount = await CountUserTablesAsync(restoredPath, cancellationToken);
        var size = new FileInfo(restoredPath).Length;
        var restoredHash = await HashFileAsync(restoredPath, cancellationToken);

        logger.LogInformation(
            "Restore de ensaio concluído: {FileName} -> {RestoredPath} ({SizeBytes} bytes, {TableCount} tabelas, sha256 {Sha256}).",
            manifest.Artifact.FileName, restoredPath, size, tableCount, restoredHash);

        return new PortalRestoreResult(restoredPath, manifest.Artifact.FileName, size, restoredHash, tableCount);
    }

    public Task DeleteAsync(string backupFileName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFileName);
        if (Path.GetFileName(backupFileName) != backupFileName)
            throw new InvalidOperationException("O nome do artefato de backup deve ser um nome de arquivo, sem diretórios.");
        var artifactPath = Path.Combine(backupDirectory, backupFileName);
        var manifestPath = artifactPath + ".json";
        var existed = File.Exists(artifactPath);
        TryDelete(artifactPath);
        TryDelete(manifestPath);
        if (existed) metrics.Deleted();
        return Task.CompletedTask;
    }

    private byte[] DecryptArtifact(byte[] artifactBytes, BackupEncryption encryption)
    {
        if (artifactBytes.Length < NonceBytes + TagBytes)
            throw new InvalidOperationException("Artefato de backup cifrado inválido.");
        var cipherSha256 = Convert.ToHexString(SHA256.HashData(artifactBytes)).ToLowerInvariant();
        if (!string.Equals(cipherSha256, encryption.CipherSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O artefato cifrado não confere com o SHA-256 registrado no manifesto.");
        var nonce = artifactBytes.AsSpan(0, NonceBytes).ToArray();
        var tag = artifactBytes.AsSpan(artifactBytes.Length - TagBytes).ToArray();
        var ciphertext = artifactBytes.AsSpan(NonceBytes, artifactBytes.Length - NonceBytes - TagBytes).ToArray();
        return protector.Open(new EncryptedBackupEnvelope(nonce, ciphertext, tag), encryption.KeyVersion);
    }

    private async Task<Manifest?> ReadManifestAsync(string backupFileName, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(backupDirectory, backupFileName + ".json");
        if (!File.Exists(manifestPath)) return null;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            return await JsonSerializer.DeserializeAsync<Manifest>(stream, SerializerOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<int> CountUserTablesAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
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

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task WriteEnvelopeAsync(string target, EncryptedBackupEnvelope envelope,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, true);
        await stream.WriteAsync(envelope.Nonce, cancellationToken);
        await stream.WriteAsync(envelope.Ciphertext, cancellationToken);
        await stream.WriteAsync(envelope.Tag, cancellationToken);
    }

    private async Task WriteManifestAsync(PortalBackupArtifact artifact, string cipherSha256,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(backupDirectory, artifact.FileName + ".json");
        var temporaryPath = manifestPath + ".tmp";
        var encryption = new BackupEncryption("aes-256-gcm", protector.ActiveKeyVersion!, cipherSha256);
        var manifest = new Manifest(ManifestSchemaVersion, artifact, Path.GetFileName(databasePath), encryption);
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

    private sealed record Manifest(int Schema, PortalBackupArtifact Artifact, string SourceDatabase,
        BackupEncryption? Encryption = null);

    private sealed record BackupEncryption(string Algorithm, string KeyVersion, string CipherSha256);
}
