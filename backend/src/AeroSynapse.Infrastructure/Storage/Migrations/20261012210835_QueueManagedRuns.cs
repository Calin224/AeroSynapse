using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class QueueManagedRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExecutionRequested",
                schema: "twin",
                table: "runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "RetryOfRunId",
                schema: "twin",
                table: "runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_runs_dispatch",
                schema: "twin",
                table: "runs",
                columns: new[] { "ExecutionRequested", "Status", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_runs_RetryOfRunId",
                schema: "twin",
                table: "runs",
                column: "RetryOfRunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runs_SceneId_RetryOfRunId",
                schema: "twin",
                table: "runs",
                columns: new[] { "SceneId", "RetryOfRunId" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_run_dispatch_inputs",
                schema: "twin",
                table: "runs",
                sql: "NOT \"ExecutionRequested\" OR \"InputSnapshotJson\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_run_retry_dispatch",
                schema: "twin",
                table: "runs",
                sql: "\"RetryOfRunId\" IS NULL OR \"ExecutionRequested\"");

            migrationBuilder.AddForeignKey(
                name: "FK_runs_runs_SceneId_RetryOfRunId",
                schema: "twin",
                table: "runs",
                columns: new[] { "SceneId", "RetryOfRunId" },
                principalSchema: "twin",
                principalTable: "runs",
                principalColumns: new[] { "SceneId", "Id" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql("""
                CREATE FUNCTION twin.protect_dispatch_identity() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE source twin.runs%ROWTYPE;
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        IF NEW."ExecutionRequested" IS DISTINCT FROM OLD."ExecutionRequested" OR
                           NEW."RetryOfRunId" IS DISTINCT FROM OLD."RetryOfRunId" THEN
                            RAISE EXCEPTION 'Execution ownership and retry provenance are immutable.' USING ERRCODE = '23514';
                        END IF;
                    ELSIF NEW."RetryOfRunId" IS NOT NULL THEN
                        SELECT * INTO source FROM twin.runs WHERE "Id" = NEW."RetryOfRunId" AND "SceneId" = NEW."SceneId" FOR SHARE;
                        IF NOT FOUND OR source."Status" <> 'failed' OR NEW."ScenarioId" IS DISTINCT FROM source."ScenarioId" OR
                           NEW."ScenarioRevision" IS DISTINCT FROM source."ScenarioRevision" OR
                           NEW."InputSnapshotJson" IS DISTINCT FROM source."InputSnapshotJson" OR
                           NEW."ParametersJson" IS DISTINCT FROM source."ParametersJson" OR NEW."Seed" IS DISTINCT FROM source."Seed" THEN
                            RAISE EXCEPTION 'Retry must preserve the exact failed run inputs.' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER protect_dispatch_identity BEFORE INSERT OR UPDATE ON twin.runs
                    FOR EACH ROW EXECUTE FUNCTION twin.protect_dispatch_identity();
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER protect_dispatch_identity ON twin.runs; DROP FUNCTION twin.protect_dispatch_identity();");
            migrationBuilder.DropForeignKey(
                name: "FK_runs_runs_SceneId_RetryOfRunId",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropIndex(
                name: "ix_runs_dispatch",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropIndex(
                name: "IX_runs_RetryOfRunId",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropIndex(
                name: "IX_runs_SceneId_RetryOfRunId",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_run_dispatch_inputs",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_run_retry_dispatch",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "ExecutionRequested",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "RetryOfRunId",
                schema: "twin",
                table: "runs");
        }
    }
}
