using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Feedback.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReplyEmailHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EmailEnqueuePending",
                table: "feedback_messages",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailEnqueuePending",
                table: "feedback_messages");
        }
    }
}
