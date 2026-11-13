using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Users.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropUsersDeadEmailFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SuppressScheduleChangeEmails",
                table: "users");

            migrationBuilder.DropColumn(
                name: "UnsubscribedFromCampaigns",
                table: "users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SuppressScheduleChangeEmails",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UnsubscribedFromCampaigns",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
