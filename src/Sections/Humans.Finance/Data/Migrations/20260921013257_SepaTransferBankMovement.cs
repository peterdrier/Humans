using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Finance.Data.Migrations
{
    /// <inheritdoc />
    public partial class SepaTransferBankMovement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HoldedBankMovementId",
                table: "sepa_payout_transfers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "ReconciledAt",
                table: "sepa_payout_transfers",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HoldedBankMovementId",
                table: "sepa_payout_transfers");

            migrationBuilder.DropColumn(
                name: "ReconciledAt",
                table: "sepa_payout_transfers");
        }
    }
}
