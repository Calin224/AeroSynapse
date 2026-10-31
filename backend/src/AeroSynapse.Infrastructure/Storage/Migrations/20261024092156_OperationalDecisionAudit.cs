using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class OperationalDecisionAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "twin",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecommendationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScenarioVersionBefore = table.Column<int>(type: "integer", nullable: true),
                    ScenarioVersionAfter = table.Column<int>(type: "integer", nullable: true),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VirtualMinute = table.Column<double>(type: "double precision", nullable: true),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false),
                    ChangesJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Sequence);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_CorrelationId_Sequence",
                schema: "twin",
                table: "audit_events",
                columns: new[] { "CorrelationId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_Id",
                schema: "twin",
                table: "audit_events",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_RecommendationId_Sequence",
                schema: "twin",
                table: "audit_events",
                columns: new[] { "RecommendationId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_RecordedAtUtc",
                schema: "twin",
                table: "audit_events",
                column: "RecordedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_RunId_Sequence",
                schema: "twin",
                table: "audit_events",
                columns: new[] { "RunId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_ScenarioId_Sequence",
                schema: "twin",
                table: "audit_events",
                columns: new[] { "ScenarioId", "Sequence" });
            using var resource = typeof(TwinDbContext).Assembly.GetManifestResourceStream(
                "AeroSynapse.Infrastructure.Storage.Migrations.Sql.OperationalDecisionAudit.sql")!;
            using var reader = new System.IO.StreamReader(resource);
            migrationBuilder.Sql(reader.ReadToEnd());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ DECLARE table_name text; BEGIN
                  FOREACH table_name IN ARRAY ARRAY['layout_versions','scenarios','runs','simulation_commands','simulation_batches','batch_variants',
                    'batch_replications','run_recommendations','recommendation_candidates','recommendation_reviews','recommendation_audit','events',
                    'operational_notifications','notification_activity','anomaly_reviews','copilot_workflows'] LOOP
                    EXECUTE format('DROP TRIGGER IF EXISTS decision_audit ON twin.%I', table_name);
                  END LOOP;
                END $$;
                DROP FUNCTION twin.audit_capture();
                DROP FUNCTION twin.audit_expire(integer);
                DROP TRIGGER audit_append_only ON twin.audit_events;
                DROP TRIGGER audit_no_truncate ON twin.audit_events;
                DROP FUNCTION twin.audit_immutable();
                DROP FUNCTION twin.audit_no_truncate();
                DROP FUNCTION twin.audit_safe(jsonb,text);
                """);
            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "twin");
        }
    }
}
