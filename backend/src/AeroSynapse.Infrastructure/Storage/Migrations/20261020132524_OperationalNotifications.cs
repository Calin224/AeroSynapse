using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class OperationalNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_commands",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReceiptJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_commands", x => x.Id);
                    table.CheckConstraint("ck_notification_command_synthetic", "\"IsSynthetic\"");
                    table.ForeignKey(
                        name: "FK_notification_commands_scenarios_ScenarioId",
                        column: x => x.ScenarioId,
                        principalSchema: "twin",
                        principalTable: "scenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "operational_notifications",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ZoneName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RuleKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WarningThreshold = table.Column<int>(type: "integer", nullable: false),
                    CriticalThreshold = table.Column<int>(type: "integer", nullable: false),
                    HorizonMinutes = table.Column<int>(type: "integer", nullable: false),
                    Level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Observations = table.Column<int>(type: "integer", nullable: false),
                    LatestFromMinute = table.Column<int>(type: "integer", nullable: false),
                    FirstObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SimulationAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    Assignee = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operational_notifications", x => x.Id);
                    table.CheckConstraint("ck_notification_counts", "\"Revision\" > 0 AND \"Observations\" > 0 AND \"LatestFromMinute\" >= 0");
                    table.CheckConstraint("ck_notification_level", "\"Level\" IN ('warning', 'critical', 'resolved')");
                    table.CheckConstraint("ck_notification_rule", "\"WarningThreshold\" >= 0 AND \"CriticalThreshold\" > \"WarningThreshold\" AND \"CriticalThreshold\" <= 100000 AND \"HorizonMinutes\" BETWEEN 15 AND 30");
                    table.CheckConstraint("ck_notification_synthetic", "\"IsSynthetic\"");
                    table.ForeignKey(
                        name: "FK_operational_notifications_runs_RunId",
                        column: x => x.RunId,
                        principalSchema: "twin",
                        principalTable: "runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_operational_notifications_scenarios_ScenarioId",
                        column: x => x.ScenarioId,
                        principalSchema: "twin",
                        principalTable: "scenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification_activity",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AlertId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Actor = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AlertRevision = table.Column<int>(type: "integer", nullable: false),
                    ObservationKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_activity", x => x.Id);
                    table.CheckConstraint("ck_notification_activity_synthetic", "\"IsSynthetic\"");
                    table.ForeignKey(
                        name: "FK_notification_activity_operational_notifications_AlertId",
                        column: x => x.AlertId,
                        principalSchema: "twin",
                        principalTable: "operational_notifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_activity_AlertId_AlertRevision",
                schema: "twin",
                table: "notification_activity",
                columns: new[] { "AlertId", "AlertRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_activity_AlertId_ObservationKey",
                schema: "twin",
                table: "notification_activity",
                columns: new[] { "AlertId", "ObservationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_activity_RecordedAtUtc_Id",
                schema: "twin",
                table: "notification_activity",
                columns: new[] { "RecordedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_commands_ScenarioId",
                schema: "twin",
                table: "notification_commands",
                column: "ScenarioId");

            migrationBuilder.CreateIndex(
                name: "IX_operational_notifications_RunId_ZoneId_RuleKey",
                schema: "twin",
                table: "operational_notifications",
                columns: new[] { "RunId", "ZoneId", "RuleKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_operational_notifications_ScenarioId_LastObservedAtUtc_Id",
                schema: "twin",
                table: "operational_notifications",
                columns: new[] { "ScenarioId", "LastObservedAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_activity",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "notification_commands",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "operational_notifications",
                schema: "twin");
        }
    }
}
