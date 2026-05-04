using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileStateAndUserEmailPrimaryUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "profiles",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_emails_UserId_PrimaryVerified",
                table: "user_emails",
                columns: new[] { "UserId", "IsNotificationTarget", "IsVerified" },
                unique: true,
                filter: "\"IsNotificationTarget\" = true AND \"IsVerified\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_user_emails_UserId_PrimaryVerified",
                table: "user_emails");

            migrationBuilder.DropColumn(
                name: "State",
                table: "profiles");
        }
    }
}
