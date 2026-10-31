using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class TraceReproducibleRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EngineBuild",
                schema: "twin",
                table: "runs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "EngineMilliseconds",
                schema: "twin",
                table: "runs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EngineVersion",
                schema: "twin",
                table: "runs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputHash",
                schema: "twin",
                table: "runs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultHash",
                schema: "twin",
                table: "runs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_runs_input_hash",
                schema: "twin",
                table: "runs",
                column: "InputHash");

            migrationBuilder.AddCheckConstraint(
                name: "ck_run_engine_time",
                schema: "twin",
                table: "runs",
                sql: "\"EngineMilliseconds\" IS NULL OR (\"EngineMilliseconds\" >= 0 AND \"EngineMilliseconds\" < 'Infinity'::float8)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_run_fingerprints",
                schema: "twin",
                table: "runs",
                sql: "(\"InputHash\" IS NULL OR \"InputHash\" ~ '^[0-9a-f]{64}$') AND (\"ResultHash\" IS NULL OR (\"Status\" = 'completed' AND \"ResultHash\" ~ '^[0-9a-f]{64}$')) AND (\"EngineBuild\" IS NULL OR \"EngineBuild\" ~ '^[0-9a-f]{64}$')");
            migrationBuilder.Sql("""
                CREATE FUNCTION twin.protect_run_provenance() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."InputHash" IS DISTINCT FROM OLD."InputHash" OR NEW."EngineVersion" IS DISTINCT FROM OLD."EngineVersion" THEN
                        RAISE EXCEPTION 'Captured input fingerprint and engine version are immutable.' USING ERRCODE = '23514';
                    END IF;
                    IF (NEW."ResultHash" IS DISTINCT FROM OLD."ResultHash" OR NEW."EngineBuild" IS DISTINCT FROM OLD."EngineBuild" OR
                        NEW."EngineMilliseconds" IS DISTINCT FROM OLD."EngineMilliseconds") AND NOT
                        (OLD."Status" = 'running' AND NEW."Status" IN ('completed', 'failed')) THEN
                        RAISE EXCEPTION 'Execution diagnostics are recorded only at completion.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER protect_run_provenance BEFORE UPDATE ON twin.runs
                    FOR EACH ROW EXECUTE FUNCTION twin.protect_run_provenance();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER protect_run_provenance ON twin.runs; DROP FUNCTION twin.protect_run_provenance();");
            migrationBuilder.DropIndex(
                name: "ix_runs_input_hash",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_run_engine_time",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_run_fingerprints",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "EngineBuild",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "EngineMilliseconds",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "EngineVersion",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "InputHash",
                schema: "twin",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "ResultHash",
                schema: "twin",
                table: "runs");
        }
    }
}
