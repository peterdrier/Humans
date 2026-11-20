using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Finance.Data.Migrations
{
    /// <inheritdoc />
    public partial class PleoBookingClaimsAndSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "finance_pleo_booking_claims",
                columns: table => new
                {
                    PleoEntryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClaimedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_finance_pleo_booking_claims", x => x.PleoEntryId);
                });

            migrationBuilder.CreateTable(
                name: "finance_pleo_receipt_uploads",
                columns: table => new
                {
                    PleoEntryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReceiptId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UploadedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Confirmed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_finance_pleo_receipt_uploads", x => new { x.PleoEntryId, x.ReceiptId });
                });

            migrationBuilder.CreateTable(
                name: "finance_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    PleoHoldedContactId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PleoHoldedTreasuryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_finance_settings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "finance_pleo_booking_claims");

            migrationBuilder.DropTable(
                name: "finance_pleo_receipt_uploads");

            migrationBuilder.DropTable(
                name: "finance_settings");
        }
    }
}
