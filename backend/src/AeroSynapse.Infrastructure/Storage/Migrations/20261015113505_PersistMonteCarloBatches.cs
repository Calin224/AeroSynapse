using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class PersistMonteCarloBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "simulation_batches",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReplayOfBatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestJson = table.Column<string>(type: "jsonb", nullable: false),
                    SeedsJson = table.Column<string>(type: "jsonb", nullable: false),
                    TimeLimitSeconds = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeadlineAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancellationRequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    EngineBuild = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_simulation_batches", x => x.Id);
                    table.CheckConstraint("ck_batch_limits", "\"TimeLimitSeconds\" BETWEEN 1 AND 3600 AND \"Revision\" > 0 AND length(btrim(\"Name\")) > 0");
                    table.CheckConstraint("ck_batch_manifest", "jsonb_typeof(\"SeedsJson\") = 'array' AND jsonb_array_length(\"SeedsJson\") BETWEEN 1 AND 25 AND jsonb_typeof(\"RequestJson\") = 'object'");
                    table.CheckConstraint("ck_batch_status", "\"Status\" IN ('pending','running','completed','completed_with_failures','cancelled','timed_out','failed')");
                    table.CheckConstraint("ck_batch_terminal", "(\"Status\" IN ('pending','running')) = (\"FinishedAtUtc\" IS NULL)");
                    table.CheckConstraint("ck_batch_time", "\"DeadlineAtUtc\" > \"CreatedAtUtc\" AND (\"FinishedAtUtc\" IS NULL OR \"FinishedAtUtc\" >= \"CreatedAtUtc\")");
                    table.ForeignKey(
                        name: "FK_simulation_batches_simulation_batches_ReplayOfBatchId",
                        column: x => x.ReplayOfBatchId,
                        principalSchema: "twin",
                        principalTable: "simulation_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "batch_variants",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    ScenarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioRevision = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    InputSnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_batch_variants", x => x.Id);
                    table.UniqueConstraint("AK_batch_variants_BatchId_Id", x => new { x.BatchId, x.Id });
                    table.CheckConstraint("ck_variant_input", "\"Position\" BETWEEN 0 AND 4 AND \"ScenarioRevision\" > 0 AND jsonb_typeof(\"InputSnapshotJson\") = 'object'");
                    table.ForeignKey(
                        name: "FK_batch_variants_scenarios_ScenarioId",
                        column: x => x.ScenarioId,
                        principalSchema: "twin",
                        principalTable: "scenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_batch_variants_simulation_batches_BatchId",
                        column: x => x.BatchId,
                        principalSchema: "twin",
                        principalTable: "simulation_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "batch_replications",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeedIndex = table.Column<int>(type: "integer", nullable: false),
                    Seed = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RemoteCancellationPending = table.Column<bool>(type: "boolean", nullable: false),
                    NextCancellationAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_batch_replications", x => x.Id);
                    table.CheckConstraint("ck_replication_seed", "\"Seed\" >= 0 AND \"SeedIndex\" BETWEEN 0 AND 24");
                    table.CheckConstraint("ck_replication_status", "\"Status\" IN ('pending','running','completed','failed','cancelled','timed_out')");
                    table.ForeignKey(
                        name: "FK_batch_replications_batch_variants_BatchId_VariantId",
                        columns: x => new { x.BatchId, x.VariantId },
                        principalSchema: "twin",
                        principalTable: "batch_variants",
                        principalColumns: new[] { "BatchId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_batch_replications_runs_RunId",
                        column: x => x.RunId,
                        principalSchema: "twin",
                        principalTable: "runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_batch_cancellations",
                schema: "twin",
                table: "batch_replications",
                columns: new[] { "RemoteCancellationPending", "NextCancellationAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_batch_replications_BatchId_VariantId",
                schema: "twin",
                table: "batch_replications",
                columns: new[] { "BatchId", "VariantId" });

            migrationBuilder.CreateIndex(
                name: "IX_batch_replications_RunId",
                schema: "twin",
                table: "batch_replications",
                column: "RunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_batch_replications_VariantId_Seed",
                schema: "twin",
                table: "batch_replications",
                columns: new[] { "VariantId", "Seed" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_batch_replications_VariantId_SeedIndex",
                schema: "twin",
                table: "batch_replications",
                columns: new[] { "VariantId", "SeedIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_batch_variants_BatchId_Position",
                schema: "twin",
                table: "batch_variants",
                columns: new[] { "BatchId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_batch_variants_BatchId_ScenarioId",
                schema: "twin",
                table: "batch_variants",
                columns: new[] { "BatchId", "ScenarioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_batch_variants_ScenarioId",
                schema: "twin",
                table: "batch_variants",
                column: "ScenarioId");

            migrationBuilder.CreateIndex(
                name: "ix_batches_dispatch",
                schema: "twin",
                table: "simulation_batches",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_simulation_batches_ReplayOfBatchId",
                schema: "twin",
                table: "simulation_batches",
                column: "ReplayOfBatchId");
            migrationBuilder.Sql("""
                CREATE FUNCTION twin.protect_batch_manifest() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF ROW(NEW."Name",NEW."RequestJson",NEW."SeedsJson",NEW."TimeLimitSeconds",NEW."CreatedAtUtc",NEW."DeadlineAtUtc",NEW."ReplayOfBatchId",NEW."IsSynthetic")
                       IS DISTINCT FROM ROW(OLD."Name",OLD."RequestJson",OLD."SeedsJson",OLD."TimeLimitSeconds",OLD."CreatedAtUtc",OLD."DeadlineAtUtc",OLD."ReplayOfBatchId",OLD."IsSynthetic") THEN
                        RAISE EXCEPTION 'Captured batch manifest is immutable.' USING ERRCODE = '23514';
                    END IF;
                    IF OLD."Status" NOT IN ('pending','running') AND NEW IS DISTINCT FROM OLD THEN
                        RAISE EXCEPTION 'Terminal batches are immutable.' USING ERRCODE = '23514';
                    END IF;
                    IF NEW IS DISTINCT FROM OLD AND NEW."Revision" <> OLD."Revision" + 1 THEN
                        RAISE EXCEPTION 'Batch revision must increase by one.' USING ERRCODE = '23514';
                    END IF;
                    IF OLD."EngineBuild" IS NOT NULL AND NEW."EngineBuild" IS DISTINCT FROM OLD."EngineBuild" THEN
                        RAISE EXCEPTION 'A batch cannot mix engine builds.' USING ERRCODE = '23514';
                    END IF;
                    IF OLD."CancellationRequestedAtUtc" IS NOT NULL AND NEW."CancellationRequestedAtUtc" IS DISTINCT FROM OLD."CancellationRequestedAtUtc" THEN
                        RAISE EXCEPTION 'Batch cancellation cannot be withdrawn.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER protect_batch_manifest BEFORE UPDATE ON twin.simulation_batches
                    FOR EACH ROW EXECUTE FUNCTION twin.protect_batch_manifest();
                CREATE FUNCTION twin.protect_batch_variant() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW IS DISTINCT FROM OLD THEN RAISE EXCEPTION 'Captured batch variants are immutable.' USING ERRCODE = '23514'; END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER protect_batch_variant BEFORE UPDATE ON twin.batch_variants
                    FOR EACH ROW EXECUTE FUNCTION twin.protect_batch_variant();
                CREATE FUNCTION twin.protect_replication_identity() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE expected_seed integer; source twin.runs%ROWTYPE; variant twin.batch_variants%ROWTYPE;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        SELECT ("SeedsJson" ->> NEW."SeedIndex")::integer INTO expected_seed FROM twin.simulation_batches WHERE "Id" = NEW."BatchId";
                        SELECT * INTO source FROM twin.runs WHERE "Id" = NEW."RunId";
                        SELECT * INTO variant FROM twin.batch_variants WHERE "Id" = NEW."VariantId";
                        IF expected_seed IS DISTINCT FROM NEW."Seed" OR source."Seed" IS DISTINCT FROM NEW."Seed" OR source."ExecutionRequested"
                           OR source."ScenarioId" IS DISTINCT FROM variant."ScenarioId" OR source."ScenarioRevision" IS DISTINCT FROM variant."ScenarioRevision" THEN
                            RAISE EXCEPTION 'Replication must preserve its captured scenario and shared seed slot.' USING ERRCODE = '23514';
                        END IF;
                    ELSE
                        IF ROW(NEW."RunId",NEW."BatchId",NEW."VariantId",NEW."Seed",NEW."SeedIndex",NEW."IsSynthetic")
                           IS DISTINCT FROM ROW(OLD."RunId",OLD."BatchId",OLD."VariantId",OLD."Seed",OLD."SeedIndex",OLD."IsSynthetic") THEN
                            RAISE EXCEPTION 'Replication identity and seed are immutable.' USING ERRCODE = '23514';
                        END IF;
                        IF OLD."Status" NOT IN ('pending','running') AND NEW."Status" IS DISTINCT FROM OLD."Status" THEN
                            RAISE EXCEPTION 'Terminal replication outcomes are immutable.' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER protect_replication_identity BEFORE INSERT OR UPDATE ON twin.batch_replications
                    FOR EACH ROW EXECUTE FUNCTION twin.protect_replication_identity();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER protect_replication_identity ON twin.batch_replications;
                DROP FUNCTION twin.protect_replication_identity();
                DROP TRIGGER protect_batch_variant ON twin.batch_variants;
                DROP FUNCTION twin.protect_batch_variant();
                DROP TRIGGER protect_batch_manifest ON twin.simulation_batches;
                DROP FUNCTION twin.protect_batch_manifest();
                """);
            migrationBuilder.DropTable(
                name: "batch_replications",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "batch_variants",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "simulation_batches",
                schema: "twin");
        }
    }
}
