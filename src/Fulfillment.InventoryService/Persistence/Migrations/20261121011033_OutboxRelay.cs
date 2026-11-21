using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.InventoryService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OutboxRelay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                schema: "inventory",
                table: "Outbox",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "EventId",
                schema: "inventory",
                table: "Outbox",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWID()");

            migrationBuilder.AddColumn<int>(
                name: "Generation",
                schema: "inventory",
                table: "Outbox",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                schema: "inventory",
                table: "Outbox",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                schema: "inventory",
                table: "Outbox",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseUntil",
                schema: "inventory",
                table: "Outbox",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                schema: "inventory",
                table: "Outbox",
                type: "datetime2(3)",
                nullable: false,
                defaultValueSql: "SYSUTCDATETIME()");

            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedAt",
                schema: "inventory",
                table: "Outbox",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SchemaVersion",
                schema: "inventory",
                table: "Outbox",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "Stalled",
                schema: "inventory",
                table: "Outbox",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Due",
                schema: "inventory",
                table: "Outbox",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "UX_Outbox_EventId",
                schema: "inventory",
                table: "Outbox",
                column: "EventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Outbox_Due",
                schema: "inventory",
                table: "Outbox");

            migrationBuilder.DropIndex(
                name: "UX_Outbox_EventId",
                schema: "inventory",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "Attempts",
                schema: "inventory",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "EventId",
                schema: "inventory",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "Generation",
                schema: "inventory",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "LastError",
                schema: "inventory",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                schema: "inventory",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "LeaseUntil",
                schema: "inventory",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                schema: "inventory",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                schema: "inventory",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "SchemaVersion",
                schema: "inventory",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "Stalled",
                schema: "inventory",
                table: "Outbox");
        }
    }
}
