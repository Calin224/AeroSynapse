using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ControlledCopilotPlaybooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "copilot_workflows",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: false),
                    Minute = table.Column<int>(type: "integer", nullable: false),
                    PlaybookId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeadlineAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancellationRequested = table.Column<bool>(type: "boolean", nullable: false),
                    CancellationPending = table.Column<bool>(type: "boolean", nullable: false),
                    StopCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ReplayOfId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvaluationId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestJson = table.Column<string>(type: "jsonb", nullable: false),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false),
                    StepsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ConclusionJson = table.Column<string>(type: "jsonb", nullable: true),
                    ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Seed = table.Column<int>(type: "integer", nullable: false),
                    EngineVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EngineBuild = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_copilot_workflows", x => x.Id);
                    table.CheckConstraint("ck_workflow_scope", "\"Minute\" >= 0 AND \"Revision\" > 0 AND \"DeadlineAtUtc\" > \"CreatedAtUtc\"");
                    table.CheckConstraint("ck_workflow_status", "\"Status\" IN ('pending','running','completed','failed','cancelled','timed_out')");
                    table.CheckConstraint("ck_workflow_terminal", "(\"Status\" IN ('pending','running')) = (\"FinishedAtUtc\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_copilot_workflows_runs_SceneId_RunId",
                        columns: x => new { x.SceneId, x.RunId },
                        principalSchema: "twin",
                        principalTable: "runs",
                        principalColumns: new[] { "SceneId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_copilot_workflows_ScenarioId_RunId_CreatedAtUtc",
                schema: "twin",
                table: "copilot_workflows",
                columns: new[] { "ScenarioId", "RunId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_copilot_workflows_SceneId_RunId",
                schema: "twin",
                table: "copilot_workflows",
                columns: new[] { "SceneId", "RunId" });

            migrationBuilder.CreateIndex(
                name: "IX_copilot_workflows_Status_NextAttemptAtUtc",
                schema: "twin",
                table: "copilot_workflows",
                columns: new[] { "Status", "NextAttemptAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "copilot_workflows",
                schema: "twin");
        }
    }
}
