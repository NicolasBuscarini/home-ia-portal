using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageWatch.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IdentityObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 15, nullable: true),
                    ObservationHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SchemaVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ModelVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    EmbeddingSchemaVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Dimensions = table.Column<int>(type: "INTEGER", nullable: false),
                    CameraPublicId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ClipPublicId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ObservedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    IngestedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    MatchStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    BestPersonPublicId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    BestScore = table.Column<double>(type: "REAL", nullable: true),
                    CandidateMargin = table.Column<double>(type: "REAL", nullable: true),
                    EvaluatedEnrollments = table.Column<int>(type: "INTEGER", nullable: false),
                    SkippedEnrollments = table.Column<int>(type: "INTEGER", nullable: false),
                    PolicyVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PolicyThreshold = table.Column<double>(type: "REAL", nullable: false),
                    PolicyMargin = table.Column<double>(type: "REAL", nullable: false),
                    ReviewStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    ResolvedPersonPublicId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    LatestReviewPublicId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityObservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdentityObservationCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ObservationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PersonPublicId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Score = table.Column<double>(type: "REAL", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityObservationCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityObservationCandidates_IdentityObservations_ObservationId",
                        column: x => x.ObservationId,
                        principalTable: "IdentityObservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IdentityObservationReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 15, nullable: true),
                    ObservationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReviewerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReviewerPublicId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Decision = table.Column<int>(type: "INTEGER", nullable: false),
                    SelectedPersonPublicId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityObservationReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityObservationReviews_IdentityObservations_ObservationId",
                        column: x => x.ObservationId,
                        principalTable: "IdentityObservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityObservationCandidates_ObservationId_Rank",
                table: "IdentityObservationCandidates",
                columns: new[] { "ObservationId", "Rank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityObservationReviews_ObservationId_CreatedAtUtc",
                table: "IdentityObservationReviews",
                columns: new[] { "ObservationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityObservationReviews_PublicId",
                table: "IdentityObservationReviews",
                column: "PublicId",
                unique: true,
                filter: "\"PublicId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityObservations_CameraPublicId_ObservedAtUtc",
                table: "IdentityObservations",
                columns: new[] { "CameraPublicId", "ObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityObservations_ObservationHash",
                table: "IdentityObservations",
                column: "ObservationHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityObservations_PublicId",
                table: "IdentityObservations",
                column: "PublicId",
                unique: true,
                filter: "\"PublicId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityObservations_ReviewStatus_IngestedAtUtc",
                table: "IdentityObservations",
                columns: new[] { "ReviewStatus", "IngestedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdentityObservationCandidates");

            migrationBuilder.DropTable(
                name: "IdentityObservationReviews");

            migrationBuilder.DropTable(
                name: "IdentityObservations");
        }
    }
}
