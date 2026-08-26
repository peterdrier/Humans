using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Finance.Data.Migrations
{
    /// <inheritdoc />
    public partial class SepaTransferBooking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "BookedAt",
                table: "sepa_payout_transfers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BookedByUserId",
                table: "sepa_payout_transfers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HoldedPaymentRefs",
                table: "sepa_payout_transfers",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BookedAt",
                table: "sepa_payout_transfers");

            migrationBuilder.DropColumn(
                name: "BookedByUserId",
                table: "sepa_payout_transfers");

            migrationBuilder.DropColumn(
                name: "HoldedPaymentRefs",
                table: "sepa_payout_transfers");
        }
    }
}
