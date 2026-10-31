using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialDigitalTwin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "twin");

            migrationBuilder.CreateTable(
                name: "scenes",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scenes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "runs",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Seed = table.Column<int>(type: "integer", nullable: false),
                    ParametersJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runs", x => x.Id);
                    table.UniqueConstraint("AK_runs_SceneId_Id", x => new { x.SceneId, x.Id });
                    table.CheckConstraint("ck_run_seed", "\"Seed\" >= 0");
                    table.CheckConstraint("ck_run_status", "\"Status\" IN ('planned', 'running', 'completed', 'failed')");
                    table.CheckConstraint("ck_run_time", "\"FinishedAtUtc\" IS NULL OR \"FinishedAtUtc\" >= \"StartedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_runs_scenes_SceneId",
                        column: x => x.SceneId,
                        principalSchema: "twin",
                        principalTable: "scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "terminals",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_terminals", x => x.Id);
                    table.UniqueConstraint("AK_terminals_SceneId_Id", x => new { x.SceneId, x.Id });
                    table.ForeignKey(
                        name: "FK_terminals_scenes_SceneId",
                        column: x => x.SceneId,
                        principalSchema: "twin",
                        principalTable: "scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "events",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    SimulationRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_events_runs_SceneId_SimulationRunId",
                        columns: x => new { x.SceneId, x.SimulationRunId },
                        principalSchema: "twin",
                        principalTable: "runs",
                        principalColumns: new[] { "SceneId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_events_scenes_SceneId",
                        column: x => x.SceneId,
                        principalSchema: "twin",
                        principalTable: "scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "checkpoints",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CapacityPerMinute = table.Column<int>(type: "integer", nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_checkpoints", x => x.Id);
                    table.UniqueConstraint("AK_checkpoints_SceneId_TerminalId_Id", x => new { x.SceneId, x.TerminalId, x.Id });
                    table.CheckConstraint("ck_checkpoint_capacity", "\"CapacityPerMinute\" > 0");
                    table.ForeignKey(
                        name: "FK_checkpoints_terminals_SceneId_TerminalId",
                        columns: x => new { x.SceneId, x.TerminalId },
                        principalSchema: "twin",
                        principalTable: "terminals",
                        principalColumns: new[] { "SceneId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gates",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Capacity = table.Column<int>(type: "integer", nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gates", x => x.Id);
                    table.UniqueConstraint("AK_gates_SceneId_TerminalId_Id", x => new { x.SceneId, x.TerminalId, x.Id });
                    table.CheckConstraint("ck_gate_capacity", "\"Capacity\" > 0");
                    table.ForeignKey(
                        name: "FK_gates_terminals_SceneId_TerminalId",
                        columns: x => new { x.SceneId, x.TerminalId },
                        principalSchema: "twin",
                        principalTable: "terminals",
                        principalColumns: new[] { "SceneId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "staff",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckpointId = table.Column<Guid>(type: "uuid", nullable: true),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff", x => x.Id);
                    table.ForeignKey(
                        name: "FK_staff_checkpoints_SceneId_TerminalId_CheckpointId",
                        columns: x => new { x.SceneId, x.TerminalId, x.CheckpointId },
                        principalSchema: "twin",
                        principalTable: "checkpoints",
                        principalColumns: new[] { "SceneId", "TerminalId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_staff_terminals_SceneId_TerminalId",
                        columns: x => new { x.SceneId, x.TerminalId },
                        principalSchema: "twin",
                        principalTable: "terminals",
                        principalColumns: new[] { "SceneId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "routes",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromCheckpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToGateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DistanceMeters = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    TravelSeconds = table.Column<int>(type: "integer", nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_routes", x => x.Id);
                    table.CheckConstraint("ck_route_distance", "\"DistanceMeters\" > 0");
                    table.CheckConstraint("ck_route_duration", "\"TravelSeconds\" > 0");
                    table.ForeignKey(
                        name: "FK_routes_checkpoints_SceneId_TerminalId_FromCheckpointId",
                        columns: x => new { x.SceneId, x.TerminalId, x.FromCheckpointId },
                        principalSchema: "twin",
                        principalTable: "checkpoints",
                        principalColumns: new[] { "SceneId", "TerminalId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_routes_gates_SceneId_TerminalId_ToGateId",
                        columns: x => new { x.SceneId, x.TerminalId, x.ToGateId },
                        principalSchema: "twin",
                        principalTable: "gates",
                        principalColumns: new[] { "SceneId", "TerminalId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_checkpoints_TerminalId_Code",
                schema: "twin",
                table: "checkpoints",
                columns: new[] { "TerminalId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_events_run_occurred",
                schema: "twin",
                table: "events",
                columns: new[] { "SimulationRunId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "ix_events_scene_occurred",
                schema: "twin",
                table: "events",
                columns: new[] { "SceneId", "OccurredAtUtc", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_events_SceneId_SimulationRunId",
                schema: "twin",
                table: "events",
                columns: new[] { "SceneId", "SimulationRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_gates_TerminalId_Code",
                schema: "twin",
                table: "gates",
                columns: new[] { "TerminalId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_routes_FromCheckpointId_ToGateId",
                schema: "twin",
                table: "routes",
                columns: new[] { "FromCheckpointId", "ToGateId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_routes_SceneId_TerminalId_FromCheckpointId",
                schema: "twin",
                table: "routes",
                columns: new[] { "SceneId", "TerminalId", "FromCheckpointId" });

            migrationBuilder.CreateIndex(
                name: "IX_routes_SceneId_TerminalId_ToGateId",
                schema: "twin",
                table: "routes",
                columns: new[] { "SceneId", "TerminalId", "ToGateId" });

            migrationBuilder.CreateIndex(
                name: "IX_routes_TerminalId_Code",
                schema: "twin",
                table: "routes",
                columns: new[] { "TerminalId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_runs_scene_started",
                schema: "twin",
                table: "runs",
                columns: new[] { "SceneId", "StartedAtUtc", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_runs_status_started",
                schema: "twin",
                table: "runs",
                columns: new[] { "Status", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "ix_scenes_code",
                schema: "twin",
                table: "scenes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scenes_synthetic_id",
                schema: "twin",
                table: "scenes",
                columns: new[] { "IsSynthetic", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_staff_SceneId_TerminalId_CheckpointId",
                schema: "twin",
                table: "staff",
                columns: new[] { "SceneId", "TerminalId", "CheckpointId" });

            migrationBuilder.CreateIndex(
                name: "IX_staff_TerminalId_Code",
                schema: "twin",
                table: "staff",
                columns: new[] { "TerminalId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_terminals_SceneId_Code",
                schema: "twin",
                table: "terminals",
                columns: new[] { "SceneId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "events",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "routes",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "staff",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "runs",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "gates",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "checkpoints",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "terminals",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "scenes",
                schema: "twin");
        }
    }
}
