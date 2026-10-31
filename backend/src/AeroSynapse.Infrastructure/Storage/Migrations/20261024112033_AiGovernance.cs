using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AiGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PolicyVersion",
                schema: "twin",
                table: "copilot_workflows",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromptVersion",
                schema: "twin",
                table: "copilot_workflows",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ai_agent_events",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorId = table.Column<string>(type: "text", nullable: false),
                    CorrelationId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Tool = table.Column<string>(type: "text", nullable: true),
                    ScenarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    PolicyVersion = table.Column<int>(type: "integer", nullable: false),
                    PromptVersion = table.Column<string>(type: "text", nullable: false),
                    PromptHash = table.Column<string>(type: "text", nullable: false),
                    ReservedUsd = table.Column<decimal>(type: "numeric(14,6)", precision: 14, scale: 6, nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: true),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: true),
                    DurationMs = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_agent_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ai_policies",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorId = table.Column<string>(type: "text", nullable: false),
                    PromptVersion = table.Column<string>(type: "text", nullable: false),
                    AllowedToolsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ModelEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    DailyCallLimit = table.Column<int>(type: "integer", nullable: false),
                    DailyBudgetUsd = table.Column<decimal>(type: "numeric(14,6)", precision: 14, scale: 6, nullable: false),
                    CallReservationUsd = table.Column<decimal>(type: "numeric(14,6)", precision: 14, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_policies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_agent_events_AttemptId_Kind",
                schema: "twin",
                table: "ai_agent_events",
                columns: new[] { "AttemptId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_agent_events_RecordedAtUtc_Kind",
                schema: "twin",
                table: "ai_agent_events",
                columns: new[] { "RecordedAtUtc", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_policies_Version",
                schema: "twin",
                table: "ai_policies",
                column: "Version",
                unique: true);
            using var resource = typeof(TwinDbContext).Assembly.GetManifestResourceStream(
                "AeroSynapse.Infrastructure.Storage.Migrations.Sql.AiGovernance.sql")!;
            using var reader = new System.IO.StreamReader(resource);
            migrationBuilder.Sql(reader.ReadToEnd());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION twin.ai_agent_expire(integer);");
            migrationBuilder.DropTable(
                name: "ai_agent_events",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "ai_policies",
                schema: "twin");

            migrationBuilder.DropColumn(
                name: "PolicyVersion",
                schema: "twin",
                table: "copilot_workflows");

            migrationBuilder.DropColumn(
                name: "PromptVersion",
                schema: "twin",
                table: "copilot_workflows");
            migrationBuilder.Sql("DROP FUNCTION twin.ai_evidence_immutable();");
        }
    }
}
