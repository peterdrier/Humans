using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HoldedLedgerSingleSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE expense_reports SET \"Status\" = 'Approved' WHERE \"Status\" IN ('SepaSent', 'Paid');");

            migrationBuilder.DropTable(
                name: "holded_creditor_balances");

            migrationBuilder.DropTable(
                name: "holded_payments");

            migrationBuilder.DropColumn(
                name: "PaidAt",
                table: "expense_reports");

            migrationBuilder.DropColumn(
                name: "SepaSentAt",
                table: "expense_reports");

            migrationBuilder.CreateTable(
                name: "holded_ledger_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryNumber = table.Column<int>(type: "integer", nullable: false),
                    Line = table.Column<int>(type: "integer", nullable: false),
                    AccountNum = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Debit = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    Credit = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    LastSyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_ledger_lines", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_holded_ledger_lines_AccountNum",
                table: "holded_ledger_lines",
                column: "AccountNum");

            migrationBuilder.CreateIndex(
                name: "IX_holded_ledger_lines_EntryNumber_Line",
                table: "holded_ledger_lines",
                columns: new[] { "EntryNumber", "Line" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holded_ledger_lines");

            migrationBuilder.AddColumn<Instant>(
                name: "PaidAt",
                table: "expense_reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "SepaSentAt",
                table: "expense_reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "holded_creditor_balances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Balance = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LastSyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SupplierAccountNum = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_creditor_balances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "holded_payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Date = table.Column<LocalDate>(type: "date", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    HoldedContactId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HoldedPaymentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastSyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_payments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_holded_creditor_balances_SupplierAccountNum",
                table: "holded_creditor_balances",
                column: "SupplierAccountNum",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_holded_payments_HoldedContactId",
                table: "holded_payments",
                column: "HoldedContactId");

            migrationBuilder.CreateIndex(
                name: "IX_holded_payments_HoldedPaymentId",
                table: "holded_payments",
                column: "HoldedPaymentId",
                unique: true);
        }
    }
}
