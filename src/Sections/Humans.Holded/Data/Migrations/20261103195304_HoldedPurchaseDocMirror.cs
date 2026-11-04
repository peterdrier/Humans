using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Holded.Data.Migrations
{
    /// <inheritdoc />
    public partial class HoldedPurchaseDocMirror : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "holded_purchase_docs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DocNumber = table.Column<string>(type: "text", nullable: false),
                    ContactName = table.Column<string>(type: "text", nullable: false),
                    ContactId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Date = table.Column<LocalDate>(type: "date", nullable: false),
                    Subtotal = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    Tax = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    Total = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    IsApproved = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    BookedAccountId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastSyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_purchase_docs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holded_purchase_docs");
        }
    }
}
