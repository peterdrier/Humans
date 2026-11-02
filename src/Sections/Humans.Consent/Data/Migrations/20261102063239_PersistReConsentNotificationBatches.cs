using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Consent.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersistReConsentNotificationBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PendingReConsentBatchId",
                table: "document_versions",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingReConsentBatchId",
                table: "document_versions");
        }
    }
}
