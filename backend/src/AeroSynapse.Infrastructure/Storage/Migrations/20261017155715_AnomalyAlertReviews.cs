using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AnomalyAlertReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "anomaly_reviews",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    SettingsKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SettingsJson = table.Column<string>(type: "jsonb", nullable: false),
                    DetectorVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CheckpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    OnsetMinute = table.Column<int>(type: "integer", nullable: false),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CommandId = table.Column<Guid>(type: "uuid", nullable: false),
                    Actor = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EpisodeJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_anomaly_reviews", x => x.Id);
                    table.CheckConstraint("ck_anomaly_review_actor", "length(btrim(\"Actor\")) > 0");
                    table.CheckConstraint("ck_anomaly_review_decision", "\"Decision\" IN ('confirmed', 'rejected')");
                    table.CheckConstraint("ck_anomaly_review_json", "jsonb_typeof(\"SettingsJson\") = 'object' AND jsonb_typeof(\"EpisodeJson\") = 'object'");
                    table.CheckConstraint("ck_anomaly_review_synthetic", "\"IsSynthetic\" AND \"OnsetMinute\" >= 0");
                    table.ForeignKey(
                        name: "FK_anomaly_reviews_runs_SceneId_RunId",
                        columns: x => new { x.SceneId, x.RunId },
                        principalSchema: "twin",
                        principalTable: "runs",
                        principalColumns: new[] { "SceneId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_anomaly_reviews_simulation_batches_BatchId",
                        column: x => x.BatchId,
                        principalSchema: "twin",
                        principalTable: "simulation_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_anomaly_reviews_BatchId",
                schema: "twin",
                table: "anomaly_reviews",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_anomaly_reviews_CommandId",
                schema: "twin",
                table: "anomaly_reviews",
                column: "CommandId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_anomaly_reviews_RunId_BatchId_SettingsKey_CheckpointId_Onse~",
                schema: "twin",
                table: "anomaly_reviews",
                columns: new[] { "RunId", "BatchId", "SettingsKey", "CheckpointId", "OnsetMinute", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_anomaly_reviews_SceneId_RunId",
                schema: "twin",
                table: "anomaly_reviews",
                columns: new[] { "SceneId", "RunId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "anomaly_reviews",
                schema: "twin");
        }
    }
}
