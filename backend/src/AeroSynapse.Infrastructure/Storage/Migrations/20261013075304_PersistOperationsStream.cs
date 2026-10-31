using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class PersistOperationsStream : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "operations_cursors",
                schema: "twin",
                columns: table => new
                {
                    ScenarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operations_cursors", x => x.ScenarioId);
                    table.ForeignKey(
                        name: "FK_operations_cursors_scenarios_ScenarioId",
                        column: x => x.ScenarioId,
                        principalSchema: "twin",
                        principalTable: "scenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "operations_events",
                schema: "twin",
                columns: table => new
                {
                    ScenarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operations_events", x => new { x.ScenarioId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_operations_events_scenarios_ScenarioId",
                        column: x => x.ScenarioId,
                        principalSchema: "twin",
                        principalTable: "scenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "run_progress",
                schema: "twin",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    SimulationMinute = table.Column<double>(type: "double precision", nullable: false),
                    ZonesJson = table.Column<string>(type: "jsonb", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_progress", x => x.RunId);
                    table.CheckConstraint("ck_progress_minute", "\"SimulationMinute\" >= 0 AND \"SimulationMinute\" <= 10080");
                    table.CheckConstraint("ck_progress_zones", "jsonb_typeof(\"ZonesJson\") = 'array' AND jsonb_array_length(\"ZonesJson\") <= 1000");
                    table.ForeignKey(
                        name: "FK_run_progress_runs_RunId",
                        column: x => x.RunId,
                        principalSchema: "twin",
                        principalTable: "runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.Sql("""
                CREATE FUNCTION twin.operations_count(value jsonb, field text) RETURNS integer LANGUAGE sql IMMUTABLE AS $$
                    SELECT CASE WHEN jsonb_typeof(value->field) = 'number' THEN
                        CASE WHEN (value->>field)::numeric BETWEEN 0 AND 2147483647 AND
                                  trunc((value->>field)::numeric) = (value->>field)::numeric
                             THEN (value->>field)::numeric::integer ELSE NULL END ELSE NULL END;
                $$;
                CREATE FUNCTION twin.operations_snapshot(zones jsonb) RETURNS jsonb LANGUAGE sql IMMUTABLE AS $$
                    SELECT jsonb_build_object('totalZones', jsonb_array_length(zones), 'zones', COALESCE(jsonb_agg(
                        jsonb_build_object('zoneId', item->>'zoneId',
                            'queuedPassengers', twin.operations_count(item, 'queuedPassengers'),
                            'processedPassengers', twin.operations_count(item, 'processedPassengers')) ORDER BY ordinal), '[]'::jsonb))
                    FROM jsonb_array_elements(zones) WITH ORDINALITY AS z(item, ordinal) WHERE ordinal <= 100;
                $$;
                CREATE FUNCTION twin.operations_emit(scenario uuid, kind text, run uuid, minute double precision,
                    status text, valid boolean, snapshot jsonb, optimization jsonb) RETURNS void LANGUAGE plpgsql AS $$
                DECLARE position bigint;
                BEGIN
                    IF scenario IS NULL THEN RETURN; END IF;
                    INSERT INTO twin.operations_cursors ("ScenarioId", "Sequence") VALUES (scenario, 0) ON CONFLICT DO NOTHING;
                    -- This row lock serializes event order with commit order for every scenario.
                    UPDATE twin.operations_cursors SET "Sequence" = "Sequence" + 1 WHERE "ScenarioId" = scenario RETURNING "Sequence" INTO position;
                    INSERT INTO twin.operations_events ("ScenarioId", "Sequence", "Type", "PayloadJson") VALUES (scenario, position, kind,
                        jsonb_build_object('scenarioId', scenario, 'sequence', position, 'type', kind, 'runId', run,
                            'simulationMinute', minute, 'status', status, 'resultsValid', valid, 'snapshot', snapshot, 'optimization', optimization));
                    DELETE FROM twin.operations_events WHERE "ScenarioId" = scenario AND "Sequence" <= position - 1024;
                END;
                $$;
                CREATE FUNCTION twin.operations_progress() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE owner twin.runs%ROWTYPE; duration integer; allowed jsonb; now_utc timestamptz := clock_timestamp(); publish boolean;
                BEGIN
                    SELECT * INTO owner FROM twin.runs WHERE "Id" = NEW."RunId" FOR UPDATE;
                    duration := (owner."InputSnapshotJson"->'scenario'->'inputs'->>'durationMinutes')::integer;
                    IF owner."Status" <> 'running' OR duration IS NULL OR NEW."SimulationMinute" > duration THEN
                        RAISE EXCEPTION 'Progress requires a running run inside its captured horizon.' USING ERRCODE = '23514';
                    END IF;
                    allowed := COALESCE(owner."InputSnapshotJson"->'layout'->'layout'->'checkpoints', '[]') ||
                               COALESCE(owner."InputSnapshotJson"->'layout'->'layout'->'gates', '[]') ||
                               COALESCE(owner."InputSnapshotJson"->'layout'->'layout'->'terminals', '[]') ||
                               jsonb_build_array(owner."InputSnapshotJson"->'layout'->'layout'->'scene');
                    IF EXISTS (SELECT 1 FROM jsonb_array_elements(NEW."ZonesJson") z WHERE NOT EXISTS
                        (SELECT 1 FROM jsonb_array_elements(allowed) zone WHERE zone->>'id' = z->>'zoneId')) THEN
                        RAISE EXCEPTION 'Progress zone does not belong to the captured layout.' USING ERRCODE = '23514';
                    END IF;
                    IF TG_OP = 'UPDATE' THEN
                        IF NEW."SimulationMinute" < OLD."SimulationMinute" THEN
                            RAISE EXCEPTION 'Progress time cannot move backwards.' USING ERRCODE = '23514';
                        END IF;
                        IF NEW."SimulationMinute" = OLD."SimulationMinute" AND NEW."ZonesJson" = OLD."ZonesJson" THEN RETURN OLD; END IF;
                        NEW."PublishedAtUtc" := OLD."PublishedAtUtc";
                        publish := OLD."PublishedAtUtc" IS NULL OR now_utc - OLD."PublishedAtUtc" >= INTERVAL '500 milliseconds'
                            OR (NEW."SimulationMinute" = duration AND NEW."SimulationMinute" > OLD."SimulationMinute");
                    ELSE publish := true;
                    END IF;
                    IF publish THEN
                        NEW."PublishedAtUtc" := now_utc;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE FUNCTION twin.operations_progress_publish() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE scenario uuid;
                BEGIN
                    -- AFTER avoids publishing the speculative INSERT of an UPSERT that becomes an UPDATE.
                    IF TG_OP = 'INSERT' OR NEW."PublishedAtUtc" IS DISTINCT FROM OLD."PublishedAtUtc" THEN
                        SELECT "ScenarioId" INTO scenario FROM twin.runs WHERE "Id" = NEW."RunId";
                        PERFORM twin.operations_emit(scenario, 'run.snapshot', NEW."RunId", NEW."SimulationMinute", 'running', false,
                            twin.operations_snapshot(NEW."ZonesJson"), NULL);
                    END IF;
                    RETURN NEW;
                END;
                $$;
                -- Existing completed/failed runs can be recovered immediately after upgrading.
                INSERT INTO twin.run_progress ("RunId", "SimulationMinute", "ZonesJson")
                    SELECT run."Id", EXTRACT(EPOCH FROM (snapshot."RecordedAtUtc" - (run."InputSnapshotJson"->'scenario'->'inputs'->>'startsAtUtc')::timestamptz)) / 60,
                        jsonb_agg(jsonb_build_object('zoneId', snapshot."ZoneId", 'queuedPassengers',
                            COALESCE(twin.operations_count(snapshot."PayloadJson", 'queuedPassengers'), twin.operations_count(snapshot."PayloadJson", 'queueLength')),
                            'processedPassengers', twin.operations_count(snapshot."PayloadJson", 'processedPassengers')) ORDER BY snapshot."ZoneId")
                    FROM twin.runs run JOIN twin.run_snapshots snapshot ON snapshot."RunId" = run."Id"
                    WHERE run."InputSnapshotJson" IS NOT NULL AND snapshot."RecordedAtUtc" =
                        (SELECT max("RecordedAtUtc") FROM twin.run_snapshots WHERE "RunId" = run."Id")
                    GROUP BY run."Id", snapshot."RecordedAtUtc" HAVING count(*) <= 1000;
                CREATE TRIGGER operations_progress BEFORE INSERT OR UPDATE ON twin.run_progress FOR EACH ROW EXECUTE FUNCTION twin.operations_progress();
                CREATE TRIGGER operations_progress_publish AFTER INSERT OR UPDATE ON twin.run_progress FOR EACH ROW EXECUTE FUNCTION twin.operations_progress_publish();

                CREATE FUNCTION twin.operations_result_snapshot() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE input jsonb; minute double precision; previous twin.run_progress%ROWTYPE; zones jsonb; zone jsonb;
                BEGIN
                    SELECT "InputSnapshotJson" INTO input FROM twin.runs WHERE "Id" = NEW."RunId";
                    IF input IS NULL THEN RETURN NEW; END IF;
                    minute := EXTRACT(EPOCH FROM (NEW."RecordedAtUtc" - (input->'scenario'->'inputs'->>'startsAtUtc')::timestamptz)) / 60;
                    SELECT * INTO previous FROM twin.run_progress WHERE "RunId" = NEW."RunId";
                    IF FOUND AND previous."SimulationMinute" > minute THEN RETURN NEW; END IF;
                    zone := jsonb_build_object('zoneId', NEW."ZoneId", 'queuedPassengers',
                        COALESCE(twin.operations_count(NEW."PayloadJson", 'queuedPassengers'), twin.operations_count(NEW."PayloadJson", 'queueLength')),
                        'processedPassengers', twin.operations_count(NEW."PayloadJson", 'processedPassengers'));
                    SELECT COALESCE(jsonb_agg(item ORDER BY item->>'zoneId'), '[]') INTO zones FROM (
                        SELECT item FROM jsonb_array_elements(CASE WHEN previous."SimulationMinute" = minute THEN previous."ZonesJson" ELSE '[]' END) item
                        WHERE item->>'zoneId' <> NEW."ZoneId"::text UNION ALL SELECT zone) entries;
                    INSERT INTO twin.run_progress ("RunId", "SimulationMinute", "ZonesJson") VALUES (NEW."RunId", minute, zones)
                        ON CONFLICT ("RunId") DO UPDATE SET "SimulationMinute" = EXCLUDED."SimulationMinute", "ZonesJson" = EXCLUDED."ZonesJson";
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER operations_result_snapshot AFTER INSERT ON twin.run_snapshots FOR EACH ROW EXECUTE FUNCTION twin.operations_result_snapshot();

                CREATE FUNCTION twin.operations_run_lifecycle() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE progress twin.run_progress%ROWTYPE;
                BEGIN
                    IF NEW."Status" = OLD."Status" OR NEW."ScenarioId" IS NULL THEN RETURN NEW; END IF;
                    SELECT * INTO progress FROM twin.run_progress WHERE "RunId" = NEW."Id";
                    IF NEW."Status" = 'running' THEN
                        PERFORM twin.operations_emit(NEW."ScenarioId", 'run.started', NEW."Id", 0, 'running', false, NULL, NULL);
                    ELSIF NEW."Status" IN ('completed', 'failed') THEN
                        PERFORM twin.operations_emit(NEW."ScenarioId", 'run.completed', NEW."Id", progress."SimulationMinute", NEW."Status", NEW."Status" = 'completed',
                            CASE WHEN progress."RunId" IS NOT NULL THEN twin.operations_snapshot(progress."ZonesJson") ELSE NULL END, NULL);
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER operations_run_lifecycle AFTER UPDATE ON twin.runs FOR EACH ROW EXECUTE FUNCTION twin.operations_run_lifecycle();

                CREATE FUNCTION twin.operations_optimization() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE scenario uuid; baseline_scenario uuid; recommended uuid; recommended_scenario uuid; local_count integer;
                BEGIN
                    SELECT "ScenarioId" INTO baseline_scenario FROM twin.runs WHERE "Id" = NEW."BaselineRunId";
                    SELECT run."Id", run."ScenarioId" INTO recommended, recommended_scenario FROM twin.recommendation_candidates candidate
                        JOIN twin.runs run ON run."Id" = candidate."RunId" WHERE candidate."RecommendationId" = NEW."Id" ORDER BY candidate."Rank" LIMIT 1;
                    FOR scenario IN SELECT DISTINCT run."ScenarioId" FROM twin.runs run WHERE run."ScenarioId" IS NOT NULL AND
                        (run."Id" = NEW."BaselineRunId" OR run."Id" IN (SELECT "RunId" FROM twin.recommendation_candidates WHERE "RecommendationId" = NEW."Id")) ORDER BY run."ScenarioId" LOOP
                        SELECT count(*) INTO local_count FROM twin.runs run WHERE run."ScenarioId" = scenario AND
                            (run."Id" = NEW."BaselineRunId" OR run."Id" IN (SELECT "RunId" FROM twin.recommendation_candidates WHERE "RecommendationId" = NEW."Id"));
                        PERFORM twin.operations_emit(scenario, 'optimization.completed', NULL, NULL, 'completed', true, NULL,
                            jsonb_build_object('optimizationId', NEW."Id", 'baselineRunId', CASE WHEN baseline_scenario = scenario THEN NEW."BaselineRunId" ELSE NULL END,
                                'recommendedRunId', CASE WHEN recommended_scenario = scenario THEN recommended ELSE NULL END, 'comparedRunCount', local_count));
                    END LOOP;
                    RETURN NEW;
                END;
                $$;
                CREATE CONSTRAINT TRIGGER operations_optimization AFTER INSERT ON twin.run_recommendations DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION twin.operations_optimization();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER operations_optimization ON twin.run_recommendations;
                DROP TRIGGER operations_run_lifecycle ON twin.runs;
                DROP TRIGGER operations_result_snapshot ON twin.run_snapshots;
                DROP TRIGGER operations_progress ON twin.run_progress;
                DROP TRIGGER operations_progress_publish ON twin.run_progress;
                DROP FUNCTION twin.operations_optimization();
                DROP FUNCTION twin.operations_run_lifecycle();
                DROP FUNCTION twin.operations_result_snapshot();
                DROP FUNCTION twin.operations_progress();
                DROP FUNCTION twin.operations_progress_publish();
                DROP FUNCTION twin.operations_emit(uuid, text, uuid, double precision, text, boolean, jsonb, jsonb);
                DROP FUNCTION twin.operations_snapshot(jsonb);
                DROP FUNCTION twin.operations_count(jsonb, text);
                """);
            migrationBuilder.DropTable(
                name: "operations_cursors",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "operations_events",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "run_progress",
                schema: "twin");
        }
    }
}
