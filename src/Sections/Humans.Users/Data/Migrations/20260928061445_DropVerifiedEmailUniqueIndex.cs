using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Users.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropVerifiedEmailUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_user_emails_Email",
                table: "user_emails");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_user_emails_Email",
                table: "user_emails",
                column: "Email",
                unique: true,
                filter: "\"IsVerified\" = true");
        }
    }
}
