namespace GarageWatch.Application.Maintenance;

/// <summary>
/// Verified artifact produced by a portal database backup. It carries only locality, size and hash:
/// the portal database stores minimized metadata and never image, video or biometric embedding.
/// </summary>
public sealed record PortalBackupArtifact(
    string FileName,
    DateTimeOffset CreatedAtUtc,
    long SizeBytes,
    string Sha256);

/// <summary>
/// Result of a restore drill performed on an isolated directory. It never touches the live
/// database; it proves that a verified artifact opens as a valid, non-empty SQLite database.
/// </summary>
public sealed record PortalRestoreResult(
    string RestoredDatabasePath,
    string SourceFileName,
    long SizeBytes,
    string Sha256,
    int TableCount);

/// <summary>
/// Read-only observability summary of the portal backups: the newest verified artifact (from the
/// durable manifests), the retained set, the scheduling posture and the last in-process failure.
/// It never carries the database content, the encryption key or free-text error details.
/// </summary>
public sealed record PortalBackupSummary(
    DateTimeOffset? LastSuccessAtUtc,
    string? LastSuccessFileName,
    long? LastSuccessSizeBytes,
    string? LastSuccessSha256,
    int ArtifactCount,
    long TotalSizeBytes,
    bool ScheduleEnabled,
    int ScheduleIntervalHours,
    int ScheduleRetentionCount,
    DateTimeOffset? LastFailureAtUtc);

/// <summary>
/// Consistent backup and isolated restore of the portal SQLite database. Restore to the live path
/// stays an operator procedure with the services stopped; <see cref="RestoreAsync"/> only proves,
/// in an isolated directory, that a verified artifact can be brought back as a working database.
/// </summary>
public interface IPortalBackupService
{
    Task<PortalBackupArtifact> CreateAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PortalBackupArtifact>> ListAsync(CancellationToken cancellationToken);

    Task<PortalRestoreResult> RestoreAsync(string backupFileName, string targetDirectory,
        CancellationToken cancellationToken);

    Task DeleteAsync(string backupFileName, CancellationToken cancellationToken);
}
