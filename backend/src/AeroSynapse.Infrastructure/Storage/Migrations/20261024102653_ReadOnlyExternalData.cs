using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ReadOnlyExternalData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DataSourceJson",
                schema: "twin",
                table: "scenarios",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "external_data_snapshots",
                schema: "twin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioRevision = table.Column<int>(type: "integer", nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false),
                    RetrievedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_data_snapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_external_data_snapshots_ScenarioId_RetrievedAtUtc",
                schema: "twin",
                table: "external_data_snapshots",
                columns: new[] { "ScenarioId", "RetrievedAtUtc" });
            using var resource = typeof(TwinDbContext).Assembly.GetManifestResourceStream(
                "AeroSynapse.Infrastructure.Storage.Migrations.Sql.ReadOnlyExternalData.sql")!;
            using var reader = new System.IO.StreamReader(resource);
            migrationBuilder.Sql(reader.ReadToEnd());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_data_snapshots",
                schema: "twin");

            migrationBuilder.Sql("DROP FUNCTION twin.external_data_expire(integer); DROP FUNCTION twin.external_snapshot_immutable();");

            migrationBuilder.DropColumn(
                name: "DataSourceJson",
                schema: "twin",
                table: "scenarios");
        }
    }
}
