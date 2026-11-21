using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.CarrierSimulator.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LostModes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Behavior_Mode",
                schema: "carrier",
                table: "Behavior");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Behavior_Mode",
                schema: "carrier",
                table: "Behavior",
                sql: "Mode IN ('Auto', 'Manual', 'Lost', 'LostNoPoll')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Behavior_Mode",
                schema: "carrier",
                table: "Behavior");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Behavior_Mode",
                schema: "carrier",
                table: "Behavior",
                sql: "Mode IN ('Auto', 'Manual')");
        }
    }
}
