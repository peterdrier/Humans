using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMailerInboundImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "SubscribedAt",
                table: "communication_preferences",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "forgotten_emails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AnonymizedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_forgotten_emails", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_forgotten_emails_AnonymizedAt",
                table: "forgotten_emails",
                column: "AnonymizedAt");

            migrationBuilder.CreateIndex(
                name: "IX_forgotten_emails_EmailHash",
                table: "forgotten_emails",
                column: "EmailHash");

            migrationBuilder.CreateIndex(
                name: "IX_forgotten_emails_UserId_EmailHash",
                table: "forgotten_emails",
                columns: new[] { "UserId", "EmailHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "forgotten_emails");

            migrationBuilder.DropColumn(
                name: "SubscribedAt",
                table: "communication_preferences");
        }
    }
}
