using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HoldedCreditorContact : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "holded_creditor_contacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    HoldedContactId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SupplierAccountNum = table.Column<int>(type: "integer", nullable: true),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_creditor_contacts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_holded_creditor_contacts_SupplierAccountNum",
                table: "holded_creditor_contacts",
                column: "SupplierAccountNum");

            migrationBuilder.CreateIndex(
                name: "IX_holded_creditor_contacts_UserId",
                table: "holded_creditor_contacts",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holded_creditor_contacts");
        }
    }
}
