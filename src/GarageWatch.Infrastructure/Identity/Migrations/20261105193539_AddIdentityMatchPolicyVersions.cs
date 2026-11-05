using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageWatch.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityMatchPolicyVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IdentityMatchPolicyVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 15, nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Threshold = table.Column<double>(type: "REAL", nullable: false),
                    Margin = table.Column<double>(type: "REAL", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    SnapshotHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FromUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ToUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CameraPublicId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    GenuineCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ImpostorCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ExcludedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EstimatedFar = table.Column<double>(type: "REAL", nullable: true),
                    EstimatedFrr = table.Column<double>(type: "REAL", nullable: true),
                    CreatedByPublicId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    PromotedByPublicId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    PromotedAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityMatchPolicyVersions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityMatchPolicyVersions_PublicId",
                table: "IdentityMatchPolicyVersions",
                column: "PublicId",
                unique: true,
                filter: "\"PublicId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityMatchPolicyVersions_Status",
                table: "IdentityMatchPolicyVersions",
                column: "Status",
                unique: true,
                filter: "\"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityMatchPolicyVersions_Version",
                table: "IdentityMatchPolicyVersions",
                column: "Version",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdentityMatchPolicyVersions");
        }
    }
}
