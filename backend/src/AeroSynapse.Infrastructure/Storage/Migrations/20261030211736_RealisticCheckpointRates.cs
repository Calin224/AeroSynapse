using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroSynapse.Infrastructure.Storage.Migrations
{
    /// <summary>
    /// The synthetic checkpoints were seeded at 7-9 passengers a minute per operator, two to three times what one
    /// screening lane handles. Published layout versions keep their captured rates; only the scene tables change.
    /// </summary>
    public partial class RealisticCheckpointRates : Migration
    {
        private static readonly (string Id, int Old, int New)[] Checkpoints =
        [
            ("60000000-0000-0000-0000-000000000101", 7, 3),
            ("60000000-0000-0000-0000-000000000102", 8, 3),
            ("60000000-0000-0000-0000-000000000103", 9, 2),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (id, old, rate) in Checkpoints)
                Set(migrationBuilder, id, old, rate);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (id, old, rate) in Checkpoints)
                Set(migrationBuilder, id, rate, old);
        }

        // Only a synthetic row still at the value being replaced changes.
        private static void Set(MigrationBuilder migrationBuilder, string id, int from, int to) => migrationBuilder.Sql(
            $"UPDATE twin.checkpoints SET \"CapacityPerMinute\" = {to} " +
            $"WHERE \"Id\" = '{id}' AND \"IsSynthetic\" AND \"CapacityPerMinute\" = {from};");
    }
}
