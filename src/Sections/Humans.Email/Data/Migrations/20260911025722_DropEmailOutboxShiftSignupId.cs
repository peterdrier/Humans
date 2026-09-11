using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Email.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropEmailOutboxShiftSignupId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_email_outbox_messages_ShiftSignupId_TemplateName",
                table: "email_outbox_messages");

            migrationBuilder.DropColumn(
                name: "ShiftSignupId",
                table: "email_outbox_messages");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ShiftSignupId",
                table: "email_outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_email_outbox_messages_ShiftSignupId_TemplateName",
                table: "email_outbox_messages",
                columns: new[] { "ShiftSignupId", "TemplateName" },
                filter: "\"ShiftSignupId\" IS NOT NULL");
        }
    }
}
