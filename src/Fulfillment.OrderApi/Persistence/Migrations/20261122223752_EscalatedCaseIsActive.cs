using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.OrderApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EscalatedCaseIsActive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_InterventionCase_OpenPerKind",
                schema: "orders",
                table: "InterventionCase");

            migrationBuilder.CreateIndex(
                name: "UX_InterventionCase_OpenPerKind",
                schema: "orders",
                table: "InterventionCase",
                columns: new[] { "OrderId", "Kind" },
                unique: true,
                filter: "[State] IN ('Open', 'Reconciling', 'Escalated')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_InterventionCase_OpenPerKind",
                schema: "orders",
                table: "InterventionCase");

            migrationBuilder.CreateIndex(
                name: "UX_InterventionCase_OpenPerKind",
                schema: "orders",
                table: "InterventionCase",
                columns: new[] { "OrderId", "Kind" },
                unique: true,
                filter: "[State] IN ('Open', 'Reconciling')");
        }
    }
}
