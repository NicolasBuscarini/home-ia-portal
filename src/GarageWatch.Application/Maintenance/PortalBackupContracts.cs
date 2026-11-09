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
/// Consistent backup of the portal SQLite database. Restore stays an operator procedure with the
/// services stopped; this boundary only creates and lists verified artifacts on an independent path.
/// </summary>
public interface IPortalBackupService
{
    Task<PortalBackupArtifact> CreateAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PortalBackupArtifact>> ListAsync(CancellationToken cancellationToken);
}
