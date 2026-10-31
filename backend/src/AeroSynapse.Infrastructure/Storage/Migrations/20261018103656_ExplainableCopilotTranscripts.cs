using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ExplainableCopilotTranscripts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "copilot_transcripts",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: false),
                    Minute = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Redacted = table.Column<bool>(type: "boolean", nullable: false),
                    Question = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ExplanationJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_copilot_transcripts", x => x.Id);
                    table.CheckConstraint("ck_transcript_minute", "\"Minute\" >= 0");
                    table.CheckConstraint("ck_transcript_redaction", "NOT \"Redacted\" OR \"Question\" IS NULL");
                    table.CheckConstraint("ck_transcript_retention", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_copilot_transcripts_runs_SceneId_RunId",
                        columns: x => new { x.SceneId, x.RunId },
                        principalSchema: "twin",
                        principalTable: "runs",
                        principalColumns: new[] { "SceneId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_copilot_transcripts_ExpiresAtUtc",
                schema: "twin",
                table: "copilot_transcripts",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_copilot_transcripts_ScenarioId_RunId_CreatedAtUtc",
                schema: "twin",
                table: "copilot_transcripts",
                columns: new[] { "ScenarioId", "RunId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_copilot_transcripts_SceneId_RunId",
                schema: "twin",
                table: "copilot_transcripts",
                columns: new[] { "SceneId", "RunId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "copilot_transcripts",
                schema: "twin");
        }
    }
}
