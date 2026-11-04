using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageWatch.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddBiometricPersonLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BiometricPersonLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnrollmentPublicId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    KnownPersonPublicId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LinkedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LinkedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UnlinkedAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiometricPersonLinks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BiometricPersonLinks_EnrollmentPublicId",
                table: "BiometricPersonLinks",
                column: "EnrollmentPublicId",
                unique: true,
                filter: "\"UnlinkedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BiometricPersonLinks_KnownPersonPublicId_UnlinkedAtUtc",
                table: "BiometricPersonLinks",
                columns: new[] { "KnownPersonPublicId", "UnlinkedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BiometricPersonLinks");
        }
    }
}
