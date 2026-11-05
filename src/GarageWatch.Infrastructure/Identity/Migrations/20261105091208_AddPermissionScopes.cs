using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageWatch.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddPermissionScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_UserAccessProfileScopes",
                table: "UserAccessProfileScopes");

            migrationBuilder.AddColumn<string>(
                name: "ScopeKind",
                table: "UserAccessProfileScopes",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ModuleId",
                table: "UserAccessProfileScopes",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // Existing rows are camera/context scopes. The default keeps the column non-null
            // while the backfill preserves every assignment during the primary-key change.
            migrationBuilder.Sql("UPDATE UserAccessProfileScopes SET ScopeKind = 'camera' WHERE ScopeKind = '';");

            migrationBuilder.AddPrimaryKey(
                name: "PK_UserAccessProfileScopes",
                table: "UserAccessProfileScopes",
                columns: new[] { "AssignmentId", "ScopeKind", "ModuleId", "ContextId", "CameraId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_UserAccessProfileScopes",
                table: "UserAccessProfileScopes");

            // Down cannot represent multiple global/module rows with empty context/camera in the
            // old key; remove them explicitly and rely on backup for full rollback.
            migrationBuilder.Sql("DELETE FROM UserAccessProfileScopes WHERE ScopeKind <> 'camera';");

            migrationBuilder.DropColumn(
                name: "ScopeKind",
                table: "UserAccessProfileScopes");

            migrationBuilder.DropColumn(
                name: "ModuleId",
                table: "UserAccessProfileScopes");

            migrationBuilder.AddPrimaryKey(
                name: "PK_UserAccessProfileScopes",
                table: "UserAccessProfileScopes",
                columns: new[] { "AssignmentId", "ContextId", "CameraId" });
        }
    }
}
