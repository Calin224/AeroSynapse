using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class PersistScenarioInputs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InputSnapshotJson",
                schema: "twin",
                table: "runs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ScenarioId",
                schema: "twin",
                table: "runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScenarioRevision",
                schema: "twin",
                table: "runs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "layout_versions",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LayoutJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_layout_versions", x => x.Id);
                    table.UniqueConstraint("AK_layout_versions_SceneId_Id", x => new { x.SceneId, x.Id });
                    table.CheckConstraint("ck_layout_document", "jsonb_typeof(\"LayoutJson\") = 'object'");
                    table.CheckConstraint("ck_layout_version", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_layout_versions_scenes_SceneId",
                        column: x => x.SceneId,
                        principalSchema: "twin",
                        principalTable: "scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "scenarios",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    LayoutVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfigurationJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scenarios", x => x.Id);
                    table.UniqueConstraint("AK_scenarios_SceneId_Id", x => new { x.SceneId, x.Id });
                    table.CheckConstraint("ck_scenario_document", "jsonb_typeof(\"ConfigurationJson\") = 'object'");
                    table.CheckConstraint("ck_scenario_revision", "\"Revision\" > 0");
                    table.CheckConstraint("ck_scenario_text", "length(btrim(\"Code\")) > 0 AND length(btrim(\"Name\")) > 0");
                    table.CheckConstraint("ck_scenario_time", "\"UpdatedAtUtc\" >= \"CreatedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_scenarios_layout_versions_SceneId_LayoutVersionId",
                        columns: x => new { x.SceneId, x.LayoutVersionId },
                        principalSchema: "twin",
                        principalTable: "layout_versions",
                        principalColumns: new[] { "SceneId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_runs_scenario_started",
                schema: "twin",
                table: "runs",
                columns: new[] { "ScenarioId", "StartedAtUtc", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_runs_SceneId_ScenarioId",
                schema: "twin",
                table: "runs",
                columns: new[] { "SceneId", "ScenarioId" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_run_snapshot",
                schema: "twin",
                table: "runs",
                sql: "(\"ScenarioId\" IS NULL AND \"ScenarioRevision\" IS NULL AND \"InputSnapshotJson\" IS NULL) OR (\"ScenarioId\" IS NOT NULL AND \"ScenarioRevision\" IS NOT NULL AND \"ScenarioRevision\" > 0 AND \"InputSnapshotJson\" IS NOT NULL AND jsonb_typeof(\"InputSnapshotJson\") = 'object')");

            migrationBuilder.CreateIndex(
                name: "IX_layout_versions_SceneId_Version",
                schema: "twin",
                table: "layout_versions",
                columns: new[] { "SceneId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_scenarios_SceneId_Code",
                schema: "twin",
                table: "scenarios",
                columns: new[] { "SceneId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_scenarios_SceneId_LayoutVersionId",
                schema: "twin",
                table: "scenarios",
                columns: new[] { "SceneId", "LayoutVersionId" });

            migrationBuilder.AddForeignKey(
                name: "FK_runs_scenarios_SceneId_ScenarioId",
                schema: "twin",
                table: "runs",
                columns: new[] { "SceneId", "ScenarioId" },
                principalSchema: "twin",
                principalTable: "scenarios",
                principalColumns: new[] { "SceneId", "Id" },
                onDelete: ReferentialAction.Restrict);

            // Keep captured inputs immutable even when a writer bypasses the service.
            migrationBuilder.Sql("""
                CREATE FUNCTION twin.protect_published_layout() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW IS DISTINCT FROM OLD THEN
                        RAISE EXCEPTION 'Published layout versions are immutable; publish a new version.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER protect_published_layout BEFORE UPDATE ON twin.layout_versions
                    FOR EACH ROW EXECUTE FUNCTION twin.protect_published_layout();

                CREATE FUNCTION twin.protect_run_inputs() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."InputSnapshotJson" IS NOT NULL AND (
                        NEW."InputSnapshotJson" IS DISTINCT FROM OLD."InputSnapshotJson" OR
                        NEW."SceneId" IS DISTINCT FROM OLD."SceneId" OR
                        NEW."ScenarioId" IS DISTINCT FROM OLD."ScenarioId" OR
                        NEW."ScenarioRevision" IS DISTINCT FROM OLD."ScenarioRevision" OR
                        NEW."ParametersJson" IS DISTINCT FROM OLD."ParametersJson" OR
                        NEW."Seed" IS DISTINCT FROM OLD."Seed") THEN
                        RAISE EXCEPTION 'Captured simulation inputs are immutable.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER protect_run_inputs BEFORE UPDATE ON twin.runs
                    FOR EACH ROW EXECUTE FUNCTION twin.protect_run_inputs();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER protect_run_inputs ON twin.runs;
                DROP FUNCTION twin.protect_run_inputs();
                DROP TRIGGER protect_published_layout ON twin.layout_versions;
                DROP FUNCTION twin.protect_published_layout();
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_runs_scenarios_SceneId_ScenarioId",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropTable(
                name: "scenarios",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "layout_versions",
                schema: "twin");

            migrationBuilder.DropIndex(
                name: "ix_runs_scenario_started",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropIndex(
                name: "IX_runs_SceneId_ScenarioId",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_run_snapshot",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "InputSnapshotJson",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "ScenarioId",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "ScenarioRevision",
                schema: "twin",
                table: "runs");
        }
    }
}
