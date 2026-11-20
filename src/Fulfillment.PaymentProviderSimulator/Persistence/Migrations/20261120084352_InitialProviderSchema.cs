using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.PaymentProviderSimulator.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialProviderSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "provider");

            migrationBuilder.CreateTable(
                name: "Fault",
                schema: "provider",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Label = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: false),
                    Match = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    Mode = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Param = table.Column<int>(type: "int", nullable: true),
                    RemainingUses = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Fault", x => x.Id);
                    table.CheckConstraint("CK_Fault_Mode", "Mode IN ('DropResponseAfterCommit', 'DelayResponse', 'StatusUnknown', 'CrashAfterCommit', 'Decline', 'ExpireAuthorization', 'RejectBeforeCommit')");
                });

            migrationBuilder.CreateTable(
                name: "Operation",
                schema: "provider",
                columns: table => new
                {
                    OperationKey = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    PayloadHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    Reference = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: true),
                    Result = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    ResultCode = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    AuthorizationExpiresAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CommittedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    FaultApplied = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: true),
                    Replays = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Operation", x => x.OperationKey);
                    table.CheckConstraint("CK_Operation_Amount", "AmountMinor >= 0");
                    table.CheckConstraint("CK_Operation_Kind", "Kind IN ('Authorize', 'Capture', 'Void', 'Refund')");
                    table.CheckConstraint("CK_Operation_Reference", "(Kind = 'Authorize' AND Reference IS NULL) OR (Kind <> 'Authorize' AND Reference IS NOT NULL)");
                    table.CheckConstraint("CK_Operation_Result", "Result IN ('Authorized', 'Declined', 'Captured', 'Voided', 'Refunded', 'NothingToVoid', 'NothingToRefund', 'Rejected')");
                });

            migrationBuilder.CreateTable(
                name: "Prohibition",
                schema: "provider",
                columns: table => new
                {
                    OperationKey = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    ByOperationKey = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Prohibition", x => x.OperationKey);
                });

            migrationBuilder.CreateTable(
                name: "StatusQuery",
                schema: "provider",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OperationKey = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    Answer = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    At = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusQuery", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Fault_Match",
                schema: "provider",
                table: "Fault",
                column: "Match");

            migrationBuilder.CreateIndex(
                name: "UX_Operation_EffectPerReference",
                schema: "provider",
                table: "Operation",
                columns: new[] { "Reference", "Kind" },
                unique: true,
                filter: "[Reference] IS NOT NULL AND [Result] IN ('Captured', 'Voided', 'Refunded')");

            migrationBuilder.CreateIndex(
                name: "IX_StatusQuery_OperationKey",
                schema: "provider",
                table: "StatusQuery",
                column: "OperationKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Fault",
                schema: "provider");

            migrationBuilder.DropTable(
                name: "Operation",
                schema: "provider");

            migrationBuilder.DropTable(
                name: "Prohibition",
                schema: "provider");

            migrationBuilder.DropTable(
                name: "StatusQuery",
                schema: "provider");
        }
    }
}
