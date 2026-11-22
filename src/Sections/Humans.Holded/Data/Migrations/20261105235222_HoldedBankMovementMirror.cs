using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Holded.Data.Migrations
{
    /// <inheritdoc />
    public partial class HoldedBankMovementMirror : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "holded_bank_movements",
                columns: table => new
                {
                    TreasuryAccountId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Date = table.Column<LocalDate>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastSyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_bank_movements", x => new { x.TreasuryAccountId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_holded_bank_movements_TreasuryAccountId_Date",
                table: "holded_bank_movements",
                columns: new[] { "TreasuryAccountId", "Date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holded_bank_movements");
        }
    }
}
