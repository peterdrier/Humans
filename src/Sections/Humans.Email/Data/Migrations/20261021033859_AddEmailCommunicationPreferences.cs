using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Email.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailCommunicationPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_communication_preferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OptedOut = table.Column<bool>(type: "boolean", nullable: true),
                    InboxEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdateSource = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SubscribedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_communication_preferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_email_communication_preferences_UserId_Category",
                table: "email_communication_preferences",
                columns: new[] { "UserId", "Category" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_communication_preferences");
        }
    }
}
