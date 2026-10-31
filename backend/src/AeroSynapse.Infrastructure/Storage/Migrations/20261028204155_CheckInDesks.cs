using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class CheckInDesks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_snapshot_zone",
                schema: "twin",
                table: "run_snapshots");

            migrationBuilder.DropCheckConstraint(
                name: "ck_metric_zone",
                schema: "twin",
                table: "run_metrics");

            migrationBuilder.CreateTable(
                name: "check_in_areas",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    DeskCount = table.Column<int>(type: "integer", nullable: false),
                    ServiceSeconds = table.Column<int>(type: "integer", nullable: false),
                    WalkingSeconds = table.Column<int>(type: "integer", nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_check_in_areas", x => x.Id);
                    table.UniqueConstraint("AK_check_in_areas_SceneId_TerminalId_Id", x => new { x.SceneId, x.TerminalId, x.Id });
                    table.CheckConstraint("ck_check_in_area_service", "\"DeskCount\" BETWEEN 1 AND 1000 AND \"ServiceSeconds\" BETWEEN 1 AND 3600 AND \"WalkingSeconds\" BETWEEN 0 AND 14400");
                    table.ForeignKey(
                        name: "FK_check_in_areas_terminals_SceneId_TerminalId",
                        columns: x => new { x.SceneId, x.TerminalId },
                        principalSchema: "twin",
                        principalTable: "terminals",
                        principalColumns: new[] { "SceneId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_snapshot_zone",
                schema: "twin",
                table: "run_snapshots",
                sql: "\"ZoneType\" IN ('scene', 'terminal', 'checkpoint', 'gate', 'checkIn') AND \"ZoneId\" <> '00000000-0000-0000-0000-000000000000'::uuid");

            migrationBuilder.AddCheckConstraint(
                name: "ck_metric_zone",
                schema: "twin",
                table: "run_metrics",
                sql: "\"ZoneType\" IN ('scene', 'terminal', 'checkpoint', 'gate', 'checkIn') AND \"ZoneId\" <> '00000000-0000-0000-0000-000000000000'::uuid");

            migrationBuilder.CreateIndex(
                name: "IX_check_in_areas_TerminalId_Code",
                schema: "twin",
                table: "check_in_areas",
                columns: new[] { "TerminalId", "Code" },
                unique: true);

            // Check-in results belong to a captured check-in area; the operations stream stays the controls' queue stream.
            migrationBuilder.Sql(ValidateRunResult("WHEN 'checkpoint' THEN 'checkpoints' WHEN 'gate' THEN 'gates' WHEN 'checkIn' THEN 'checkInAreas'"));
            migrationBuilder.Sql(OperationsResultSnapshot(SkipCheckIn));
        }

        private const string SkipCheckIn = """IF NEW."ZoneType" = 'checkIn' THEN RETURN NEW; END IF;""";

        private static string ValidateRunResult(string collections) => $$"""
                CREATE OR REPLACE FUNCTION twin.validate_run_result() RETURNS trigger LANGUAGE plpgsql AS $$
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
                        zone_collection := CASE NEW."ZoneType" WHEN 'terminal' THEN 'terminals' {{collections}} ELSE NULL END;
                        SELECT EXISTS(SELECT 1 FROM jsonb_array_elements(input->'layout'->'layout'->zone_collection) zone
                            WHERE zone->>'id' = NEW."ZoneId"::text) INTO valid_zone;
                    END IF;
                    IF valid_zone IS DISTINCT FROM true THEN
                        RAISE EXCEPTION 'Zone is not in the captured layout.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                """;

        private static string OperationsResultSnapshot(string skip) => $$"""
                CREATE OR REPLACE FUNCTION twin.operations_result_snapshot() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE input jsonb; minute double precision; previous twin.run_progress%ROWTYPE; zones jsonb; zone jsonb;
                BEGIN
                    {{skip}}
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
                """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "check_in_areas",
                schema: "twin");

            // The previous constraints do not admit check-in rows; they have no meaning without the desks.
            migrationBuilder.Sql("DELETE FROM twin.run_metrics WHERE \"ZoneType\" = 'checkIn'; DELETE FROM twin.run_snapshots WHERE \"ZoneType\" = 'checkIn';");
            migrationBuilder.Sql(ValidateRunResult("WHEN 'checkpoint' THEN 'checkpoints' WHEN 'gate' THEN 'gates'"));
            migrationBuilder.Sql(OperationsResultSnapshot(""));

            migrationBuilder.DropCheckConstraint(
                name: "ck_snapshot_zone",
                schema: "twin",
                table: "run_snapshots");

            migrationBuilder.DropCheckConstraint(
                name: "ck_metric_zone",
                schema: "twin",
                table: "run_metrics");

            migrationBuilder.AddCheckConstraint(
                name: "ck_snapshot_zone",
                schema: "twin",
                table: "run_snapshots",
                sql: "\"ZoneType\" IN ('scene', 'terminal', 'checkpoint', 'gate') AND \"ZoneId\" <> '00000000-0000-0000-0000-000000000000'::uuid");

            migrationBuilder.AddCheckConstraint(
                name: "ck_metric_zone",
                schema: "twin",
                table: "run_metrics",
                sql: "\"ZoneType\" IN ('scene', 'terminal', 'checkpoint', 'gate') AND \"ZoneId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
        }
    }
}
