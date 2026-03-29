using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddShiftSignupDedupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_email_outbox_messages_ShiftSignupId",
                table: "email_outbox_messages");

            migrationBuilder.CreateIndex(
                name: "IX_email_outbox_messages_ShiftSignupId_TemplateName",
                table: "email_outbox_messages",
                columns: new[] { "ShiftSignupId", "TemplateName" },
                filter: "\"ShiftSignupId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_email_outbox_messages_ShiftSignupId_TemplateName",
                table: "email_outbox_messages");

            migrationBuilder.CreateIndex(
                name: "IX_email_outbox_messages_ShiftSignupId",
                table: "email_outbox_messages",
                column: "ShiftSignupId");
        }
    }
}
