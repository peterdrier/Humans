using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HoldedCreditor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HoldedContactId",
                table: "expense_reports",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HoldedSupplierAccountNum",
                table: "expense_reports",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "holded_creditor_balances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierAccountNum = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Balance = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    LastSyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
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
                    HoldedPaymentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HoldedContactId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    Date = table.Column<LocalDate>(type: "date", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LastSyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_payments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_expense_reports_HoldedContactId",
                table: "expense_reports",
                column: "HoldedContactId");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holded_creditor_balances");

            migrationBuilder.DropTable(
                name: "holded_payments");

            migrationBuilder.DropIndex(
                name: "IX_expense_reports_HoldedContactId",
                table: "expense_reports");

            migrationBuilder.DropColumn(
                name: "HoldedContactId",
                table: "expense_reports");

            migrationBuilder.DropColumn(
                name: "HoldedSupplierAccountNum",
                table: "expense_reports");
        }
    }
}
