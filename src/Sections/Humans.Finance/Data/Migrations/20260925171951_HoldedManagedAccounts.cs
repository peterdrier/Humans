using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Finance.Data.Migrations
{
    /// <inheritdoc />
    public partial class HoldedManagedAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "holded_managed_accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HoldedAccountNumber = table.Column<int>(type: "integer", nullable: false),
                    HoldedAccountId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_managed_accounts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_holded_managed_accounts_HoldedAccountId",
                table: "holded_managed_accounts",
                column: "HoldedAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_holded_managed_accounts_HoldedAccountNumber",
                table: "holded_managed_accounts",
                column: "HoldedAccountNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holded_managed_accounts");
        }
    }
}
