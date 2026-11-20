using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.InventoryService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HoldExtension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessedOperation_Operation",
                schema: "inventory",
                table: "ProcessedOperation");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessedOperation_Outcome",
                schema: "inventory",
                table: "ProcessedOperation");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Outbox_EventType",
                schema: "inventory",
                table: "Outbox");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessedOperation_Operation",
                schema: "inventory",
                table: "ProcessedOperation",
                sql: "[Operation] IN ('Reserve', 'Release', 'Consume', 'Extend')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessedOperation_Outcome",
                schema: "inventory",
                table: "ProcessedOperation",
                sql: "[Outcome] IN ('Reserved', 'InsufficientStock', 'OrderClosed', 'Released', 'NotHeld', 'Consumed', 'HoldExpired', 'Incomplete', 'Extended', 'InsufficientValidity')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Outbox_EventType",
                schema: "inventory",
                table: "Outbox",
                sql: "[EventType] IN ('HoldCreated', 'HoldReleased', 'HoldExpired', 'HoldConsumed', 'HoldExtended')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessedOperation_Operation",
                schema: "inventory",
                table: "ProcessedOperation");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessedOperation_Outcome",
                schema: "inventory",
                table: "ProcessedOperation");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Outbox_EventType",
                schema: "inventory",
                table: "Outbox");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessedOperation_Operation",
                schema: "inventory",
                table: "ProcessedOperation",
                sql: "[Operation] IN ('Reserve', 'Release', 'Consume')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessedOperation_Outcome",
                schema: "inventory",
                table: "ProcessedOperation",
                sql: "[Outcome] IN ('Reserved', 'InsufficientStock', 'OrderClosed', 'Released', 'NotHeld', 'Consumed', 'HoldExpired', 'Incomplete')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Outbox_EventType",
                schema: "inventory",
                table: "Outbox",
                sql: "[EventType] IN ('HoldCreated', 'HoldReleased', 'HoldExpired', 'HoldConsumed')");
        }
    }
}
