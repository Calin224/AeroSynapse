using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AnonymousPassengerFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "passenger_feedback_counts",
                schema: "twin",
                columns: table => new
                {
                    Usefulness = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Responses = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_passenger_feedback_counts", x => x.Usefulness);
                    table.CheckConstraint("ck_passenger_feedback_choice", "\"Usefulness\" IN ('useful', 'partly', 'not_useful')");
                    table.CheckConstraint("ck_passenger_feedback_count", "\"Responses\" >= 0");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "passenger_feedback_counts",
                schema: "twin");
        }
    }
}
