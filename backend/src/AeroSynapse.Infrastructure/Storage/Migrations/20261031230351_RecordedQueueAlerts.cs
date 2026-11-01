using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RecordedQueueAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_rule",
                schema: "twin",
                table: "operational_notifications");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_rule",
                schema: "twin",
                table: "operational_notifications",
                sql: "\"WarningThreshold\" >= 0 AND \"CriticalThreshold\" > \"WarningThreshold\" AND \"CriticalThreshold\" <= 100000 AND (\"HorizonMinutes\" BETWEEN 15 AND 30 OR (\"Source\" = 'recorded_queue' AND \"HorizonMinutes\" = 0))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_rule",
                schema: "twin",
                table: "operational_notifications");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_rule",
                schema: "twin",
                table: "operational_notifications",
                sql: "\"WarningThreshold\" >= 0 AND \"CriticalThreshold\" > \"WarningThreshold\" AND \"CriticalThreshold\" <= 100000 AND \"HorizonMinutes\" BETWEEN 15 AND 30");
        }
    }
}
