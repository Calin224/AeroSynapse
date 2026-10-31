using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class IntegrateSimulationExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_run_lifecycle",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_run_status",
                schema: "twin",
                table: "runs");

            migrationBuilder.CreateTable(
                name: "simulation_commands",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioRevision = table.Column<int>(type: "integer", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestJson = table.Column<string>(type: "jsonb", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DispatchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_simulation_commands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_simulation_commands_runs_RunId",
                        column: x => x.RunId,
                        principalSchema: "twin",
                        principalTable: "runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_run_lifecycle",
                schema: "twin",
                table: "runs",
                sql: "(\"Status\" IN ('planned', 'pending') AND \"LaunchedAtUtc\" IS NULL AND \"FinishedAtUtc\" IS NULL AND \"FailureReason\" IS NULL) OR (\"Status\" = 'running' AND \"LaunchedAtUtc\" IS NOT NULL AND \"FinishedAtUtc\" IS NULL AND \"FailureReason\" IS NULL) OR (\"Status\" = 'completed' AND \"LaunchedAtUtc\" IS NOT NULL AND \"FinishedAtUtc\" IS NOT NULL AND \"FailureReason\" IS NULL) OR (\"Status\" = 'failed' AND \"LaunchedAtUtc\" IS NOT NULL AND \"FinishedAtUtc\" IS NOT NULL AND \"FailureReason\" IS NOT NULL AND length(btrim(\"FailureReason\")) > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_run_status",
                schema: "twin",
                table: "runs",
                sql: "\"Status\" IN ('planned', 'pending', 'running', 'completed', 'failed')");

            migrationBuilder.CreateIndex(
                name: "IX_simulation_commands_FinishedAtUtc_NextAttemptAtUtc",
                schema: "twin",
                table: "simulation_commands",
                columns: new[] { "FinishedAtUtc", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_simulation_commands_RunId",
                schema: "twin",
                table: "simulation_commands",
                column: "RunId",
                unique: true);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    EXECUTE replace(pg_get_functiondef('twin.protect_run_inputs'::regproc),
                        'OLD."Status" = ''planned'' AND NEW."Status" = ''running''',
                        'OLD."Status" IN (''planned'', ''pending'') AND NEW."Status" = ''running''');
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE twin.runs DISABLE TRIGGER protect_run_inputs;
                UPDATE twin.runs SET "Status" = 'planned' WHERE "Status" = 'pending';
                ALTER TABLE twin.runs ENABLE TRIGGER protect_run_inputs;
                DO $$ BEGIN
                    EXECUTE replace(pg_get_functiondef('twin.protect_run_inputs'::regproc),
                        'OLD."Status" IN (''planned'', ''pending'') AND NEW."Status" = ''running''',
                        'OLD."Status" = ''planned'' AND NEW."Status" = ''running''');
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "simulation_commands",
                schema: "twin");

            migrationBuilder.DropCheckConstraint(
                name: "ck_run_lifecycle",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_run_status",
                schema: "twin",
                table: "runs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_run_lifecycle",
                schema: "twin",
                table: "runs",
                sql: "(\"Status\" = 'planned' AND \"LaunchedAtUtc\" IS NULL AND \"FinishedAtUtc\" IS NULL AND \"FailureReason\" IS NULL) OR (\"Status\" = 'running' AND \"LaunchedAtUtc\" IS NOT NULL AND \"FinishedAtUtc\" IS NULL AND \"FailureReason\" IS NULL) OR (\"Status\" = 'completed' AND \"LaunchedAtUtc\" IS NOT NULL AND \"FinishedAtUtc\" IS NOT NULL AND \"FailureReason\" IS NULL) OR (\"Status\" = 'failed' AND \"LaunchedAtUtc\" IS NOT NULL AND \"FinishedAtUtc\" IS NOT NULL AND \"FailureReason\" IS NOT NULL AND length(btrim(\"FailureReason\")) > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_run_status",
                schema: "twin",
                table: "runs",
                sql: "\"Status\" IN ('planned', 'running', 'completed', 'failed')");
        }
    }
}
