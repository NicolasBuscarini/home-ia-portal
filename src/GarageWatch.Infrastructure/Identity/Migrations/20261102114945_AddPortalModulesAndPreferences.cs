using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageWatch.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddPortalModulesAndPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PortalModules",
                columns: table => new
                {
                    ModuleId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DisplayNameKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ContractVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ApiBaseUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    HealthPath = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RequiredPermission = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    MenuGroup = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MenuOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    MenuIcon = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MenuLabelKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortalModules", x => x.ModuleId);
                });

            migrationBuilder.CreateTable(
                name: "PortalUserPreferences",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ThemeId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LanguageTag = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortalUserPreferences", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_PortalUserPreferences_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PortalModules_IsEnabled_MenuGroup_MenuOrder",
                table: "PortalModules",
                columns: new[] { "IsEnabled", "MenuGroup", "MenuOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PortalModules");

            migrationBuilder.DropTable(
                name: "PortalUserPreferences");
        }
    }
}
