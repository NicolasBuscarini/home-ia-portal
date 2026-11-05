using GarageWatch.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class AccessProfileScopeBackfillTests
{
    [Fact]
    public async Task Permission_scope_migration_preserves_legacy_camera_rows_and_backfills_kind()
    {
        var path = Path.Combine(Path.GetTempPath(), $"home-ia-scope-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<IamDbContext>()
                .UseSqlite($"Data Source={path};Foreign Keys=False")
                .Options;
            var assignmentId = Guid.NewGuid();
            await using var context = new IamDbContext(options, new StubPublicIds());
            await context.Database.GetService<IMigrator>().MigrateAsync("AddPlatformEvents");
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"UserAccessProfileScopes\" (\"AssignmentId\",\"ContextId\",\"CameraId\") " +
                "VALUES ({0},{1},{2})",
                assignmentId, "garage", "garage-01");

            await context.Database.MigrateAsync();

            var rows = await context.Database.SqlQueryRaw<ScopeRow>(
                    "SELECT \"AssignmentId\" AS \"AssignmentId\", \"ScopeKind\" AS \"ScopeKind\", " +
                    "\"ModuleId\" AS \"ModuleId\", \"ContextId\" AS \"ContextId\", \"CameraId\" AS \"CameraId\" " +
                    "FROM \"UserAccessProfileScopes\"")
                .ToListAsync();
            var row = Assert.Single(rows);
            Assert.Equal(assignmentId, row.AssignmentId);
            Assert.Equal("camera", row.ScopeKind);
            Assert.Equal("", row.ModuleId);
            Assert.Equal("garage", row.ContextId);
            Assert.Equal("garage-01", row.CameraId);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // SQLite may still hold the pooled handle; the file lives under the temp directory.
            }
        }
    }

    private sealed class ScopeRow
    {
        public Guid AssignmentId { get; set; }
        public string ScopeKind { get; set; } = "";
        public string ModuleId { get; set; } = "";
        public string ContextId { get; set; } = "";
        public string CameraId { get; set; } = "";
    }

    private sealed class StubPublicIds : IPublicIdGenerator
    {
        public string Generate(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..15].ToUpperInvariant();
    }
}
