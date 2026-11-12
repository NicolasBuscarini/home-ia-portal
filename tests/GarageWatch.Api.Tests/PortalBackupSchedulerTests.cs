using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Maintenance;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class PortalBackupSchedulerTests
{
    [Fact]
    public async Task Scheduler_does_nothing_when_disabled()
    {
        using var root = new TempDirectory();
        var databasePath = Path.Combine(root.Path, "portal.db");
        await CreateDatabaseAsync(databasePath);
        var backups = CreateBackupService(databasePath, Path.Combine(root.Path, "backups"));
        var scheduler = new PortalBackupSchedulerService(backups, new PortalBackupScheduleOptions(false, 24, 14),
            NullLogger<PortalBackupSchedulerService>.Instance);

        Assert.Null(await scheduler.RunOnceAsync(CancellationToken.None));
        Assert.Empty(await backups.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Scheduler_creates_a_backup_and_prunes_to_the_retention_count()
    {
        using var root = new TempDirectory();
        var databasePath = Path.Combine(root.Path, "portal.db");
        await CreateDatabaseAsync(databasePath);
        var backups = CreateBackupService(databasePath, Path.Combine(root.Path, "backups"));
        var scheduler = new PortalBackupSchedulerService(backups, new PortalBackupScheduleOptions(true, 1, 2),
            NullLogger<PortalBackupSchedulerService>.Instance);

        for (var i = 0; i < 3; i++)
        {
            await backups.CreateAsync(CancellationToken.None);
            await Task.Delay(10);
        }

        var created = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.NotNull(created);
        var remaining = await backups.ListAsync(CancellationToken.None);
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, item => item.FileName == created!.FileName);
    }

    [Fact]
    public async Task Delete_removes_artifact_and_manifest()
    {
        using var root = new TempDirectory();
        var databasePath = Path.Combine(root.Path, "portal.db");
        await CreateDatabaseAsync(databasePath);
        var backups = CreateBackupService(databasePath, Path.Combine(root.Path, "backups"));
        var artifact = await backups.CreateAsync(CancellationToken.None);

        await backups.DeleteAsync(artifact.FileName, CancellationToken.None);

        Assert.Empty(await backups.ListAsync(CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(root.Path, "backups", artifact.FileName)));
        Assert.False(File.Exists(Path.Combine(root.Path, "backups", artifact.FileName + ".json")));
    }

    private static async Task CreateDatabaseAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText = "CREATE TABLE Sample (Id INTEGER PRIMARY KEY);";
        await seed.ExecuteNonQueryAsync();
    }

    private static PortalBackupService CreateBackupService(string databasePath, string backupDirectory)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IdentityBootstrapOptions.DatabasePathKey] = databasePath,
                [PortalBackupService.BackupDirectoryKey] = backupDirectory
            })
            .Build();
        return new PortalBackupService(configuration, TimeProvider.System,
            NullLogger<PortalBackupService>.Instance);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"portal-scheduler-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
