using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using GarageWatch.Application.Maintenance;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Maintenance;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class PortalBackupTests
{
    [Fact]
    public async Task Backup_is_consistent_verified_and_listed_with_manifest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"portal-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "portal.db");
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var seed = connection.CreateCommand();
                seed.CommandText =
                    "CREATE TABLE Sample (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);" +
                    "INSERT INTO Sample (Name) VALUES ('alice');";
                await seed.ExecuteNonQueryAsync();
            }

            var backupDirectory = Path.Combine(root, "backups");
            var service = CreateService(databasePath, backupDirectory);

            var artifact = await service.CreateAsync(CancellationToken.None);

            var artifactPath = Path.Combine(backupDirectory, artifact.FileName);
            Assert.True(File.Exists(artifactPath));
            Assert.Equal(new FileInfo(artifactPath).Length, artifact.SizeBytes);
            Assert.True(File.Exists(artifactPath + ".json"));
            await using (var hashStream = File.OpenRead(artifactPath))
            {
                var expected = Convert.ToHexString(await SHA256.HashDataAsync(hashStream)).ToLowerInvariant();
                Assert.Equal(expected, artifact.Sha256);
            }

            await using var copy = new SqliteConnection($"Data Source={artifactPath};Mode=ReadOnly");
            await copy.OpenAsync();
            await using var read = copy.CreateCommand();
            read.CommandText = "SELECT Name FROM Sample";
            Assert.Equal("alice", await read.ExecuteScalarAsync());

            var listed = await service.ListAsync(CancellationToken.None);
            var listedArtifact = Assert.Single(listed);
            Assert.Equal(artifact.FileName, listedArtifact.FileName);
            Assert.Equal(artifact.Sha256, listedArtifact.Sha256);
            Assert.Equal(artifact.SizeBytes, listedArtifact.SizeBytes);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Listing_ignores_manifests_whose_artifact_was_moved_or_deleted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"portal-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "portal.db");
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var seed = connection.CreateCommand();
                seed.CommandText = "CREATE TABLE Sample (Id INTEGER PRIMARY KEY);";
                await seed.ExecuteNonQueryAsync();
            }

            var backupDirectory = Path.Combine(root, "backups");
            var service = CreateService(databasePath, backupDirectory);
            var artifact = await service.CreateAsync(CancellationToken.None);
            File.Delete(Path.Combine(backupDirectory, artifact.FileName));

            Assert.Empty(await service.ListAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Administrator_creates_a_backup_only_with_same_origin_and_the_action_is_audited()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "Contract-Password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);

        using var withoutOrigin = await client.PostAsync("/api/v1/admin/maintenance/backups", null);
        Assert.Equal(HttpStatusCode.Forbidden, withoutOrigin.StatusCode);

        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var created = await client.PostAsync("/api/v1/admin/maintenance/backups", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var artifact = await created.Content.ReadFromJsonAsync<PortalBackupArtifact>();
        Assert.NotNull(artifact);
        Assert.StartsWith("portal-", artifact!.FileName);
        Assert.Matches("^[0-9a-f]{64}$", artifact.Sha256);
        Assert.True(artifact.SizeBytes > 0);

        using var listed = await client.GetAsync("/api/v1/admin/maintenance/backups");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var artifacts = await listed.Content.ReadFromJsonAsync<PortalBackupArtifact[]>();
        Assert.Contains(artifacts!, item => item.FileName == artifact.FileName && item.Sha256 == artifact.Sha256);

        using var scope = factory.Services.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<IamDbContext>().PlatformEvents
            .AsNoTracking()
            .Where(item => item.EventType == "platform.backup.created")
            .ToArrayAsync();
        var recorded = Assert.Single(events);
        Assert.Equal("success", recorded.Outcome);
        Assert.Equal("portal_backup", recorded.ResourceType);
        Assert.Equal(artifact.FileName, recorded.ResourceId);
    }

    [Fact]
    public async Task Restore_drill_recreates_a_working_database_in_an_isolated_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"portal-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "portal.db");
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var seed = connection.CreateCommand();
                seed.CommandText =
                    "CREATE TABLE Sample (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);" +
                    "INSERT INTO Sample (Name) VALUES ('alice');";
                await seed.ExecuteNonQueryAsync();
            }

            var backupDirectory = Path.Combine(root, "backups");
            var service = CreateService(databasePath, backupDirectory);
            var artifact = await service.CreateAsync(CancellationToken.None);

            var targetDirectory = Path.Combine(root, "restored");
            var result = await service.RestoreAsync(artifact.FileName, targetDirectory, CancellationToken.None);

            Assert.Equal(Path.Combine(targetDirectory, artifact.FileName), result.RestoredDatabasePath);
            Assert.Equal(artifact.FileName, result.SourceFileName);
            Assert.Equal(artifact.Sha256, result.Sha256);
            Assert.Equal(artifact.SizeBytes, result.SizeBytes);
            Assert.Equal(1, result.TableCount);

            await using var restored = new SqliteConnection($"Data Source={result.RestoredDatabasePath};Mode=ReadOnly");
            await restored.OpenAsync();
            await using var read = restored.CreateCommand();
            read.CommandText = "SELECT Name FROM Sample";
            Assert.Equal("alice", await read.ExecuteScalarAsync());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Restore_refuses_when_the_artifact_does_not_match_the_manifest_hash()
    {
        var root = Path.Combine(Path.GetTempPath(), $"portal-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "portal.db");
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var seed = connection.CreateCommand();
                seed.CommandText = "CREATE TABLE Sample (Id INTEGER PRIMARY KEY);";
                await seed.ExecuteNonQueryAsync();
            }

            var backupDirectory = Path.Combine(root, "backups");
            var service = CreateService(databasePath, backupDirectory);
            var artifact = await service.CreateAsync(CancellationToken.None);
            var artifactPath = Path.Combine(backupDirectory, artifact.FileName);
            await File.AppendAllTextAsync(artifactPath, "tampered");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RestoreAsync(artifact.FileName, Path.Combine(root, "restored"), CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Restore_refuses_when_the_manifest_is_missing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"portal-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "portal.db");
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var seed = connection.CreateCommand();
                seed.CommandText = "CREATE TABLE Sample (Id INTEGER PRIMARY KEY);";
                await seed.ExecuteNonQueryAsync();
            }

            var backupDirectory = Path.Combine(root, "backups");
            var service = CreateService(databasePath, backupDirectory);
            var artifact = await service.CreateAsync(CancellationToken.None);
            File.Delete(Path.Combine(backupDirectory, artifact.FileName + ".json"));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RestoreAsync(artifact.FileName, Path.Combine(root, "restored"), CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static PortalBackupService CreateService(string databasePath, string backupDirectory)
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
}
