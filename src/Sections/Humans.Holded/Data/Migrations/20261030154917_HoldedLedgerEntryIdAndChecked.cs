using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Holded.Data.Migrations
{
    /// <inheritdoc />
    public partial class HoldedLedgerEntryIdAndChecked : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Checked",
                table: "holded_ledger_lines",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntryId",
                table: "holded_ledger_lines",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Checked",
                table: "holded_ledger_lines");

            migrationBuilder.DropColumn(
                name: "EntryId",
                table: "holded_ledger_lines");
        }
    }
}
