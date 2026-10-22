using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Email.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailOutboxUrgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Urgent",
                table: "email_outbox_messages",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Urgent",
                table: "email_outbox_messages");
        }
    }
}
