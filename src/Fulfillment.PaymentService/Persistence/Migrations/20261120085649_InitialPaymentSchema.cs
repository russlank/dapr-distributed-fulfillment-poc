using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.PaymentService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPaymentSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "payment");

            migrationBuilder.CreateTable(
                name: "PaymentOperation",
                schema: "payment",
                columns: table => new
                {
                    OperationKey = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    Reference = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: true),
                    State = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Outcome = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: true),
                    ResultCode = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    AuthorizationExpiresAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    FinishedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    FinishedBy = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: true),
                    Replays = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentOperation", x => x.OperationKey);
                    table.CheckConstraint("CK_PaymentOperation_Amount", "AmountMinor >= 0");
                    table.CheckConstraint("CK_PaymentOperation_Finished", "(State = 'Finished' AND Outcome IS NOT NULL AND FinishedAt IS NOT NULL) OR (State = 'Pending' AND Outcome IS NULL AND FinishedAt IS NULL)");
                    table.CheckConstraint("CK_PaymentOperation_Kind", "Kind IN ('Authorize', 'Capture', 'Void', 'Refund')");
                    table.CheckConstraint("CK_PaymentOperation_Outcome", "Outcome IS NULL OR Outcome IN ('Authorized', 'Declined', 'Captured', 'Voided', 'Refunded', 'NothingToCompensate', 'Rejected', 'Prohibited', 'Unavailable', 'Ambiguous')");
                    table.CheckConstraint("CK_PaymentOperation_State", "State IN ('Pending', 'Finished')");
                });

            migrationBuilder.CreateTable(
                name: "PaymentAttempt",
                schema: "payment",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OperationKey = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                    Answer = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: true),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Replica = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    EndedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentAttempt", x => x.Id);
                    table.CheckConstraint("CK_PaymentAttempt_Kind", "Kind IN ('Send', 'Status')");
                    table.ForeignKey(
                        name: "FK_PaymentAttempt_PaymentOperation_OperationKey",
                        column: x => x.OperationKey,
                        principalSchema: "payment",
                        principalTable: "PaymentOperation",
                        principalColumn: "OperationKey",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempt_OperationKey",
                schema: "payment",
                table: "PaymentAttempt",
                column: "OperationKey");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentOperation_OrderId",
                schema: "payment",
                table: "PaymentOperation",
                column: "OrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentAttempt",
                schema: "payment");

            migrationBuilder.DropTable(
                name: "PaymentOperation",
                schema: "payment");
        }
    }
}
