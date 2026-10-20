using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Users.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropUserMagicLinkSentAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MagicLinkSentAt",
                table: "users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "MagicLinkSentAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
