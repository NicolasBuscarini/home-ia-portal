using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageWatch.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddPortalModuleSubmenus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PortalModuleMenuItems",
                columns: table => new
                {
                    ModuleId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ItemId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LabelKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    RequiredPermission = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortalModuleMenuItems", x => new { x.ModuleId, x.ItemId });
                    table.ForeignKey(
                        name: "FK_PortalModuleMenuItems_PortalModules_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "PortalModules",
                        principalColumn: "ModuleId",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PortalModuleMenuItems");
        }
    }
}
