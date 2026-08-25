using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Finance.Data.Migrations
{
    /// <inheritdoc />
    public partial class SepaPayoutFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sepa_payout_files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GeneratedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    GeneratedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Xml = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sepa_payout_files", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sepa_payout_transfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierAccountNum = table.Column<int>(type: "integer", nullable: false),
                    CreditorName = table.Column<string>(type: "character varying(70)", maxLength: 70, nullable: false),
                    Iban = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    IbanMasked = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sepa_payout_transfers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sepa_payout_files_GeneratedAt",
                table: "sepa_payout_files",
                column: "GeneratedAt");

            migrationBuilder.CreateIndex(
                name: "IX_sepa_payout_transfers_FileId",
                table: "sepa_payout_transfers",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_sepa_payout_transfers_UserId",
                table: "sepa_payout_transfers",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sepa_payout_files");

            migrationBuilder.DropTable(
                name: "sepa_payout_transfers");
        }
    }
}
