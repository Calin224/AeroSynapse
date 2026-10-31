using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class HumanRecommendationReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recommendation_reviews",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    EvaluationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioRevision = table.Column<int>(type: "integer", nullable: false),
                    CandidateRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExplanationJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recommendation_reviews", x => x.Id);
                    table.UniqueConstraint("AK_recommendation_reviews_SceneId_Id", x => new { x.SceneId, x.Id });
                    table.CheckConstraint("ck_review_explanation", "jsonb_typeof(\"ExplanationJson\") = 'object'");
                    table.CheckConstraint("ck_review_status", "\"Status\" IN ('pending', 'approved', 'rejected')");
                    table.CheckConstraint("ck_review_synthetic", "\"IsSynthetic\" AND \"ScenarioRevision\" > 0 AND \"CandidateRunId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_recommendation_reviews_run_recommendations_SceneId_Id",
                        columns: x => new { x.SceneId, x.Id },
                        principalSchema: "twin",
                        principalTable: "run_recommendations",
                        principalColumns: new[] { "SceneId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recommendation_reviews_runs_SceneId_CandidateRunId",
                        columns: x => new { x.SceneId, x.CandidateRunId },
                        principalSchema: "twin",
                        principalTable: "runs",
                        principalColumns: new[] { "SceneId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recommendation_reviews_scenarios_SceneId_ScenarioId",
                        columns: x => new { x.SceneId, x.ScenarioId },
                        principalSchema: "twin",
                        principalTable: "scenarios",
                        principalColumns: new[] { "SceneId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recommendation_reviews_simulation_batches_EvaluationId",
                        column: x => x.EvaluationId,
                        principalSchema: "twin",
                        principalTable: "simulation_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recommendation_audit",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecommendationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Actor = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ScenarioRevisionBefore = table.Column<int>(type: "integer", nullable: false),
                    ScenarioRevisionAfter = table.Column<int>(type: "integer", nullable: false),
                    BeforeJson = table.Column<string>(type: "jsonb", nullable: false),
                    AfterJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recommendation_audit", x => x.Id);
                    table.CheckConstraint("ck_review_audit_action", "\"Action\" IN ('created', 'approved', 'rejected')");
                    table.CheckConstraint("ck_review_audit_actor", "length(btrim(\"Actor\")) > 0");
                    table.ForeignKey(
                        name: "FK_recommendation_audit_recommendation_reviews_SceneId_Recomme~",
                        columns: x => new { x.SceneId, x.RecommendationId },
                        principalSchema: "twin",
                        principalTable: "recommendation_reviews",
                        principalColumns: new[] { "SceneId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_audit_CommandId",
                schema: "twin",
                table: "recommendation_audit",
                column: "CommandId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_audit_RecommendationId",
                schema: "twin",
                table: "recommendation_audit",
                column: "RecommendationId",
                unique: true,
                filter: "\"Action\" <> 'created'");

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_audit_RecommendationId_RecordedAtUtc",
                schema: "twin",
                table: "recommendation_audit",
                columns: new[] { "RecommendationId", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_audit_SceneId_RecommendationId",
                schema: "twin",
                table: "recommendation_audit",
                columns: new[] { "SceneId", "RecommendationId" });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_reviews_EvaluationId",
                schema: "twin",
                table: "recommendation_reviews",
                column: "EvaluationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_reviews_SceneId_CandidateRunId",
                schema: "twin",
                table: "recommendation_reviews",
                columns: new[] { "SceneId", "CandidateRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_reviews_SceneId_ScenarioId",
                schema: "twin",
                table: "recommendation_reviews",
                columns: new[] { "SceneId", "ScenarioId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recommendation_audit",
                schema: "twin");

            migrationBuilder.DropTable(
                name: "recommendation_reviews",
                schema: "twin");
        }
    }
}
