using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageWatch.Infrastructure.Identity.Migrations
{
    /// <summary>
    /// Renames the legacy IAM audit table in place and extends it into the append-only platform
    /// event store. New columns stay nullable because the idempotent startup backfill fills them;
    /// the unique indexes are created immediately, which is safe while values are still null.
    /// </summary>
    public partial class AddPlatformEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "IamAuditEvents",
                newName: "PlatformEvents");

            migrationBuilder.RenameColumn(
                name: "Action",
                table: "PlatformEvents",
                newName: "EventType");

            migrationBuilder.AddColumn<string>(
                name: "PublicId",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EventId",
                table: "PlatformEvents",
                type: "TEXT",
                nullable: false,
                defaultValue: Guid.Empty);

            migrationBuilder.AddColumn<string>(
                name: "SchemaVersion",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Severity",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceVersion",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Emitter",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResourceType",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CausationId",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestId",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionId",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceId",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpanId",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HttpMethod",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HttpRoute",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HttpStatusCode",
                table: "PlatformEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DurationMs",
                table: "PlatformEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientUserAgent",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientBrowser",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientOs",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientDevice",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientIp",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataJson",
                table: "PlatformEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RecordedAtUtc",
                table: "PlatformEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayloadHash",
                table: "PlatformEvents",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.DropIndex(
                name: "IX_IamAuditEvents_ActorUserId",
                table: "PlatformEvents");

            migrationBuilder.DropIndex(
                name: "IX_IamAuditEvents_SubjectUserId_OccurredAtUtc",
                table: "PlatformEvents");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformEvents_ActorUserId_OccurredAtUtc",
                table: "PlatformEvents",
                columns: new[] { "ActorUserId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformEvents_CorrelationId",
                table: "PlatformEvents",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformEvents_EventType_OccurredAtUtc",
                table: "PlatformEvents",
                columns: new[] { "EventType", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformEvents_OccurredAtUtc_Id",
                table: "PlatformEvents",
                columns: new[] { "OccurredAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformEvents_Outcome_OccurredAtUtc",
                table: "PlatformEvents",
                columns: new[] { "Outcome", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformEvents_PublicId",
                table: "PlatformEvents",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformEvents_ResourceType_ResourceId",
                table: "PlatformEvents",
                columns: new[] { "ResourceType", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformEvents_Source_EventId",
                table: "PlatformEvents",
                columns: new[] { "Source", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformEvents_SubjectUserId_OccurredAtUtc",
                table: "PlatformEvents",
                columns: new[] { "SubjectUserId", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlatformEvents_ActorUserId_OccurredAtUtc",
                table: "PlatformEvents");

            migrationBuilder.DropIndex(
                name: "IX_PlatformEvents_CorrelationId",
                table: "PlatformEvents");

            migrationBuilder.DropIndex(
                name: "IX_PlatformEvents_EventType_OccurredAtUtc",
                table: "PlatformEvents");

            migrationBuilder.DropIndex(
                name: "IX_PlatformEvents_OccurredAtUtc_Id",
                table: "PlatformEvents");

            migrationBuilder.DropIndex(
                name: "IX_PlatformEvents_Outcome_OccurredAtUtc",
                table: "PlatformEvents");

            migrationBuilder.DropIndex(
                name: "IX_PlatformEvents_PublicId",
                table: "PlatformEvents");

            migrationBuilder.DropIndex(
                name: "IX_PlatformEvents_ResourceType_ResourceId",
                table: "PlatformEvents");

            migrationBuilder.DropIndex(
                name: "IX_PlatformEvents_Source_EventId",
                table: "PlatformEvents");

            migrationBuilder.DropIndex(
                name: "IX_PlatformEvents_SubjectUserId_OccurredAtUtc",
                table: "PlatformEvents");

            foreach (var column in new[]
                     {
                         "PublicId", "EventId", "SchemaVersion", "Category", "Severity", "Outcome",
                         "Source", "SourceVersion", "Emitter", "ResourceType", "CorrelationId",
                         "CausationId", "RequestId", "SessionId", "TraceId", "SpanId", "HttpMethod",
                         "HttpRoute", "HttpStatusCode", "DurationMs", "ClientUserAgent", "ClientBrowser",
                         "ClientOs", "ClientDevice", "ClientIp", "MetadataJson", "RecordedAtUtc",
                         "PayloadHash"
                     })
                migrationBuilder.DropColumn(name: column, table: "PlatformEvents");

            migrationBuilder.RenameColumn(
                name: "EventType",
                table: "PlatformEvents",
                newName: "Action");

            migrationBuilder.RenameTable(
                name: "PlatformEvents",
                newName: "IamAuditEvents");

            migrationBuilder.CreateIndex(
                name: "IX_IamAuditEvents_ActorUserId",
                table: "IamAuditEvents",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_IamAuditEvents_SubjectUserId_OccurredAtUtc",
                table: "IamAuditEvents",
                columns: new[] { "SubjectUserId", "OccurredAtUtc" });
        }
    }
}
