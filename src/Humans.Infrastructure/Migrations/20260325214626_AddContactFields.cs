using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContactFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContactSource",
                table: "users",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSourceId",
                table: "users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_ContactSource_ExternalSourceId",
                table: "users",
                columns: new[] { "ContactSource", "ExternalSourceId" },
                filter: "\"ExternalSourceId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_ContactSource_ExternalSourceId",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ContactSource",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ExternalSourceId",
                table: "users");
        }
    }
}
