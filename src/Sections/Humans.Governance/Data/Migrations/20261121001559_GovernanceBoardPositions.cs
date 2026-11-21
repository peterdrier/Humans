using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Governance.Data.Migrations
{
    /// <inheritdoc />
    public partial class GovernanceBoardPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "governance_board_positions",
                columns: table => new
                {
                    Position = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_governance_board_positions", x => x.Position);
                });

            migrationBuilder.CreateIndex(
                name: "IX_governance_board_positions_UserId",
                table: "governance_board_positions",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "governance_board_positions");
        }
    }
}
