using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class PersistRunResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                schema: "twin",
                table: "scenarios",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureReason",
                schema: "twin",
                table: "runs",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LaunchedAtUtc",
                schema: "twin",
                table: "runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                schema: "twin",
                table: "runs",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "run_metrics",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ZoneType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Value = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Unit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_metrics", x => x.Id);
                    table.CheckConstraint("ck_metric_text", "length(btrim(\"Name\")) > 0 AND length(btrim(\"Unit\")) > 0");
                    table.CheckConstraint("ck_metric_zone", "\"ZoneType\" IN ('scene', 'terminal', 'checkpoint', 'gate') AND \"ZoneId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_run_metrics_runs_SceneId_RunId",
                        columns: x => new { x.SceneId, x.RunId },
                        principalSchema: "twin",
                        principalTable: "runs",
                        principalColumns: new[] { "SceneId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "run_recommendations",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Objective = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    BaselineScore = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_recommendations", x => x.Id);
                    table.UniqueConstraint("AK_run_recommendations_SceneId_Id", x => new { x.SceneId, x.Id });
                    table.CheckConstraint("ck_recommendation_text", "length(btrim(\"Objective\")) > 0 AND length(btrim(\"Rationale\")) > 0");
                    table.ForeignKey(
                        name: "FK_run_recommendations_runs_SceneId_BaselineRunId",
                        columns: x => new { x.SceneId, x.BaselineRunId },
                        principalSchema: "twin",
                        principalTable: "runs",
                        principalColumns: new[] { "SceneId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "run_snapshots",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ZoneType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_snapshots", x => x.Id);
                    table.CheckConstraint("ck_snapshot_payload", "jsonb_typeof(\"PayloadJson\") = 'object'");
                    table.CheckConstraint("ck_snapshot_zone", "\"ZoneType\" IN ('scene', 'terminal', 'checkpoint', 'gate') AND \"ZoneId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_run_snapshots_runs_SceneId_RunId",
                        columns: x => new { x.SceneId, x.RunId },
                        principalSchema: "twin",
                        principalTable: "runs",
                        principalColumns: new[] { "SceneId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recommendation_candidates",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecommendationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    Score = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    AssessmentJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recommendation_candidates", x => x.Id);
                    table.CheckConstraint("ck_candidate_assessment", "jsonb_typeof(\"AssessmentJson\") = 'object'");
                    table.CheckConstraint("ck_candidate_rank", "\"Rank\" > 0");
                    table.ForeignKey(
                        name: "FK_recommendation_candidates_run_recommendations_SceneId_Recom~",
                        columns: x => new { x.SceneId, x.RecommendationId },
                        principalSchema: "twin",
                        principalTable: "run_recommendations",
                        principalColumns: new[] { "SceneId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recommendation_candidates_runs_SceneId_RunId",
                        columns: x => new { x.SceneId, x.RunId },
                        principalSchema: "twin",
                        principalTable: "runs",
                        principalColumns: new[] { "SceneId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_scenario_deleted",
                schema: "twin",
                table: "scenarios",
                sql: "\"DeletedAtUtc\" IS NULL OR \"DeletedAtUtc\" >= \"CreatedAtUtc\"");

            // Legacy runs keep their inputs; annotate unavailable failure reasons explicitly.
            migrationBuilder.Sql("""
                UPDATE twin.runs SET
                    "LaunchedAtUtc" = CASE WHEN "Status" = 'planned' THEN NULL ELSE "StartedAtUtc" END,
                    "FinishedAtUtc" = CASE WHEN "Status" IN ('planned', 'running') THEN NULL ELSE COALESCE("FinishedAtUtc", "StartedAtUtc") END,
                    "FailureReason" = CASE WHEN "Status" = 'failed' THEN 'Legacy failure: original reason unavailable.' ELSE NULL END;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_run_launch_time",
                schema: "twin",
                table: "runs",
                sql: "\"LaunchedAtUtc\" IS NULL OR (\"LaunchedAtUtc\" >= \"StartedAtUtc\" AND (\"FinishedAtUtc\" IS NULL OR \"FinishedAtUtc\" >= \"LaunchedAtUtc\"))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_run_lifecycle",
                schema: "twin",
                table: "runs",
                sql: "(\"Status\" = 'planned' AND \"LaunchedAtUtc\" IS NULL AND \"FinishedAtUtc\" IS NULL AND \"FailureReason\" IS NULL) OR (\"Status\" = 'running' AND \"LaunchedAtUtc\" IS NOT NULL AND \"FinishedAtUtc\" IS NULL AND \"FailureReason\" IS NULL) OR (\"Status\" = 'completed' AND \"LaunchedAtUtc\" IS NOT NULL AND \"FinishedAtUtc\" IS NOT NULL AND \"FailureReason\" IS NULL) OR (\"Status\" = 'failed' AND \"LaunchedAtUtc\" IS NOT NULL AND \"FinishedAtUtc\" IS NOT NULL AND \"FailureReason\" IS NOT NULL AND length(btrim(\"FailureReason\")) > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_run_revision",
                schema: "twin",
                table: "runs",
                sql: "\"Revision\" > 0");

            migrationBuilder.CreateIndex(
                name: "ix_candidates_run_recommendation",
                schema: "twin",
                table: "recommendation_candidates",
                columns: new[] { "RunId", "RecommendationId" });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_candidates_RecommendationId_Rank",
                schema: "twin",
                table: "recommendation_candidates",
                columns: new[] { "RecommendationId", "Rank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_candidates_RecommendationId_RunId",
                schema: "twin",
                table: "recommendation_candidates",
                columns: new[] { "RecommendationId", "RunId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_candidates_SceneId_RecommendationId",
                schema: "twin",
                table: "recommendation_candidates",
                columns: new[] { "SceneId", "RecommendationId" });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_candidates_SceneId_RunId",
                schema: "twin",
                table: "recommendation_candidates",
                columns: new[] { "SceneId", "RunId" });

            migrationBuilder.CreateIndex(
                name: "ix_metrics_run_time",
                schema: "twin",
                table: "run_metrics",
                columns: new[] { "RunId", "RecordedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "ix_metrics_run_zone_time_name",
                schema: "twin",
                table: "run_metrics",
                columns: new[] { "RunId", "ZoneType", "ZoneId", "RecordedAtUtc", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_run_metrics_SceneId_RunId",
                schema: "twin",
                table: "run_metrics",
                columns: new[] { "SceneId", "RunId" });

            migrationBuilder.CreateIndex(
                name: "ix_recommendations_baseline_created",
                schema: "twin",
                table: "run_recommendations",
                columns: new[] { "BaselineRunId", "CreatedAtUtc", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_run_recommendations_SceneId_BaselineRunId",
                schema: "twin",
                table: "run_recommendations",
                columns: new[] { "SceneId", "BaselineRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_run_snapshots_SceneId_RunId",
                schema: "twin",
                table: "run_snapshots",
                columns: new[] { "SceneId", "RunId" });

            migrationBuilder.CreateIndex(
                name: "ix_snapshots_run_time",
                schema: "twin",
                table: "run_snapshots",
                columns: new[] { "RunId", "RecordedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "ix_snapshots_run_zone_time",
                schema: "twin",
                table: "run_snapshots",
                columns: new[] { "RunId", "ZoneType", "ZoneId", "RecordedAtUtc" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION twin.protect_run_inputs() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."Status" IN ('completed', 'failed') AND NEW IS DISTINCT FROM OLD THEN
                        RAISE EXCEPTION 'Terminal runs are immutable.' USING ERRCODE = '23514';
                    END IF;
                    IF NEW."Status" IS DISTINCT FROM OLD."Status" AND NOT (
                        (OLD."Status" = 'planned' AND NEW."Status" = 'running') OR
                        (OLD."Status" = 'running' AND NEW."Status" IN ('completed', 'failed'))) THEN
                        RAISE EXCEPTION 'Invalid run status transition.' USING ERRCODE = '23514';
                    END IF;
                    IF NEW."Revision" <> OLD."Revision" + (CASE WHEN NEW."Status" IS DISTINCT FROM OLD."Status" THEN 1 ELSE 0 END) THEN
                        RAISE EXCEPTION 'Run revision must follow the status transition.' USING ERRCODE = '23514';
                    END IF;
                    IF OLD."LaunchedAtUtc" IS NOT NULL AND NEW."LaunchedAtUtc" IS DISTINCT FROM OLD."LaunchedAtUtc" THEN
                        RAISE EXCEPTION 'Run launch time is immutable.' USING ERRCODE = '23514';
                    END IF;
                    IF (OLD."InputSnapshotJson" IS NOT NULL OR OLD."Status" <> 'planned') AND (
                        NEW."InputSnapshotJson" IS DISTINCT FROM OLD."InputSnapshotJson" OR
                        NEW."SceneId" IS DISTINCT FROM OLD."SceneId" OR
                        NEW."ScenarioId" IS DISTINCT FROM OLD."ScenarioId" OR
                        NEW."ScenarioRevision" IS DISTINCT FROM OLD."ScenarioRevision" OR
                        NEW."ParametersJson" IS DISTINCT FROM OLD."ParametersJson" OR
                        NEW."StartedAtUtc" IS DISTINCT FROM OLD."StartedAtUtc" OR
                        NEW."Seed" IS DISTINCT FROM OLD."Seed") THEN
                        RAISE EXCEPTION 'Captured or launched simulation inputs are immutable.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE FUNCTION twin.validate_run_owner() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE archived timestamptz;
                BEGIN
                    IF NEW."ScenarioId" IS NOT NULL THEN
                        SELECT "DeletedAtUtc" INTO archived FROM twin.scenarios
                            WHERE "SceneId" = NEW."SceneId" AND "Id" = NEW."ScenarioId" FOR UPDATE;
                        IF NOT FOUND THEN RAISE EXCEPTION 'Run scenario does not exist.' USING ERRCODE = '23503'; END IF;
                        IF archived IS NOT NULL THEN RAISE EXCEPTION 'Archived scenarios cannot create new runs.' USING ERRCODE = '23514'; END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER validate_run_owner BEFORE INSERT ON twin.runs FOR EACH ROW EXECUTE FUNCTION twin.validate_run_owner();

                CREATE FUNCTION twin.validate_run_result() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE run_status text; input jsonb; zone_collection text; valid_zone boolean;
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        IF NEW IS DISTINCT FROM OLD THEN RAISE EXCEPTION 'Run results are append-only.' USING ERRCODE = '23514'; END IF;
                        RETURN NEW;
                    END IF;
                    SELECT "Status", "InputSnapshotJson" INTO run_status, input FROM twin.runs
                        WHERE "SceneId" = NEW."SceneId" AND "Id" = NEW."RunId" FOR UPDATE;
                    IF NOT FOUND THEN RAISE EXCEPTION 'Result run does not exist in this scene.' USING ERRCODE = '23503'; END IF;
                    IF run_status <> 'running' OR input IS NULL THEN
                        RAISE EXCEPTION 'Results require a running run with captured inputs.' USING ERRCODE = '23514';
                    END IF;
                    IF NEW."RecordedAtUtc" < (input->'scenario'->'inputs'->>'startsAtUtc')::timestamptz OR
                        NEW."RecordedAtUtc" > (input->'scenario'->'inputs'->>'startsAtUtc')::timestamptz +
                            (input->'scenario'->'inputs'->>'durationMinutes')::int * INTERVAL '1 minute' THEN
                        RAISE EXCEPTION 'Result timestamp is outside the captured simulation window.' USING ERRCODE = '23514';
                    END IF;
                    IF NEW."ZoneType" = 'scene' THEN
                        valid_zone := input->'layout'->'layout'->'scene'->>'id' = NEW."ZoneId"::text;
                    ELSE
                        zone_collection := CASE NEW."ZoneType" WHEN 'terminal' THEN 'terminals' WHEN 'checkpoint' THEN 'checkpoints' WHEN 'gate' THEN 'gates' ELSE NULL END;
                        SELECT EXISTS(SELECT 1 FROM jsonb_array_elements(input->'layout'->'layout'->zone_collection) zone
                            WHERE zone->>'id' = NEW."ZoneId"::text) INTO valid_zone;
                    END IF;
                    IF valid_zone IS DISTINCT FROM true THEN
                        RAISE EXCEPTION 'Zone is not in the captured layout.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER validate_run_metric BEFORE INSERT OR UPDATE ON twin.run_metrics FOR EACH ROW EXECUTE FUNCTION twin.validate_run_result();
                CREATE TRIGGER validate_run_snapshot BEFORE INSERT OR UPDATE ON twin.run_snapshots FOR EACH ROW EXECUTE FUNCTION twin.validate_run_result();

                CREATE FUNCTION twin.validate_recommendation() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE baseline twin.runs%ROWTYPE;
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        IF NEW IS DISTINCT FROM OLD THEN RAISE EXCEPTION 'Recommendations are immutable.' USING ERRCODE = '23514'; END IF;
                        RETURN NEW;
                    END IF;
                    SELECT * INTO baseline FROM twin.runs WHERE "Id" = NEW."BaselineRunId" AND "SceneId" = NEW."SceneId" FOR SHARE;
                    IF NOT FOUND THEN RAISE EXCEPTION 'Baseline does not exist in this scene.' USING ERRCODE = '23503'; END IF;
                    IF baseline."Status" <> 'completed' OR baseline."InputSnapshotJson" IS NULL THEN
                        RAISE EXCEPTION 'Baseline must be completed with captured inputs.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER validate_recommendation BEFORE INSERT OR UPDATE ON twin.run_recommendations FOR EACH ROW EXECUTE FUNCTION twin.validate_recommendation();

                CREATE FUNCTION twin.validate_recommendation_candidate() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE baseline_id uuid; baseline twin.runs%ROWTYPE; candidate twin.runs%ROWTYPE;
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        IF NEW IS DISTINCT FROM OLD THEN RAISE EXCEPTION 'Compared candidates are immutable.' USING ERRCODE = '23514'; END IF;
                        RETURN NEW;
                    END IF;
                    SELECT "BaselineRunId" INTO baseline_id FROM twin.run_recommendations
                        WHERE "Id" = NEW."RecommendationId" AND "SceneId" = NEW."SceneId";
                    IF NOT FOUND THEN RAISE EXCEPTION 'Recommendation does not exist in this scene.' USING ERRCODE = '23503'; END IF;
                    SELECT * INTO baseline FROM twin.runs WHERE "Id" = baseline_id FOR SHARE;
                    SELECT * INTO candidate FROM twin.runs WHERE "Id" = NEW."RunId" AND "SceneId" = NEW."SceneId" FOR SHARE;
                    IF NOT FOUND THEN RAISE EXCEPTION 'Candidate does not exist in this scene.' USING ERRCODE = '23503'; END IF;
                    IF candidate."Id" = baseline_id OR candidate."Status" <> 'completed' OR candidate."InputSnapshotJson" IS NULL THEN
                        RAISE EXCEPTION 'Candidate must be a different completed run with captured inputs.' USING ERRCODE = '23514';
                    END IF;
                    IF baseline."InputSnapshotJson"->'layout'->'version'->>'id' IS DISTINCT FROM candidate."InputSnapshotJson"->'layout'->'version'->>'id' OR
                        baseline."InputSnapshotJson"->'scenario'->'inputs'->>'startsAtUtc' IS DISTINCT FROM candidate."InputSnapshotJson"->'scenario'->'inputs'->>'startsAtUtc' OR
                        baseline."InputSnapshotJson"->'scenario'->'inputs'->>'durationMinutes' IS DISTINCT FROM candidate."InputSnapshotJson"->'scenario'->'inputs'->>'durationMinutes' THEN
                        RAISE EXCEPTION 'Candidates require the baseline layout and simulation window.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER validate_recommendation_candidate BEFORE INSERT OR UPDATE ON twin.recommendation_candidates
                    FOR EACH ROW EXECUTE FUNCTION twin.validate_recommendation_candidate();

                CREATE FUNCTION twin.require_recommendation_candidates() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE recommendation_id uuid;
                BEGIN
                    IF TG_TABLE_NAME = 'run_recommendations' THEN recommendation_id := NEW."Id";
                    ELSE recommendation_id := OLD."RecommendationId"; END IF;
                    IF EXISTS(SELECT 1 FROM twin.run_recommendations WHERE "Id" = recommendation_id) AND
                        NOT EXISTS(SELECT 1 FROM twin.recommendation_candidates WHERE "RecommendationId" = recommendation_id) THEN
                        RAISE EXCEPTION 'A recommendation must retain at least one compared candidate.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NULL;
                END;
                $$;
                CREATE CONSTRAINT TRIGGER require_recommendation_candidates AFTER INSERT ON twin.run_recommendations
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION twin.require_recommendation_candidates();
                CREATE CONSTRAINT TRIGGER retain_recommendation_candidates AFTER DELETE ON twin.recommendation_candidates
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION twin.require_recommendation_candidates();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER validate_run_owner ON twin.runs;
                DROP FUNCTION twin.validate_run_owner();
                DROP TRIGGER validate_run_metric ON twin.run_metrics;
                DROP TRIGGER validate_run_snapshot ON twin.run_snapshots;
                DROP FUNCTION twin.validate_run_result();
                DROP TRIGGER validate_recommendation ON twin.run_recommendations;
                DROP FUNCTION twin.validate_recommendation();
                DROP TRIGGER validate_recommendation_candidate ON twin.recommendation_candidates;
                DROP FUNCTION twin.validate_recommendation_candidate();
                DROP TRIGGER require_recommendation_candidates ON twin.run_recommendations;
                DROP TRIGGER retain_recommendation_candidates ON twin.recommendation_candidates;
                DROP FUNCTION twin.require_recommendation_candidates();
                CREATE OR REPLACE FUNCTION twin.protect_run_inputs() RETURNS trigger LANGUAGE plpgsql AS $$
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
                """);
            migrationBuilder.DropTable(
                name: "recommendation_candidates",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "run_metrics",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "run_snapshots",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "run_recommendations",
                schema: "twin");

            migrationBuilder.DropCheckConstraint(
                name: "ck_scenario_deleted",
                schema: "twin",
                table: "scenarios");

            migrationBuilder.DropCheckConstraint(
                name: "ck_run_launch_time",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_run_lifecycle",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_run_revision",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                schema: "twin",
                table: "scenarios");

            migrationBuilder.DropColumn(
                name: "FailureReason",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "LaunchedAtUtc",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "Revision",
                schema: "twin",
                table: "runs");
        }
    }
}
