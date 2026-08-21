using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.MailerLite.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialMailerLiteSection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mailerlite_sync_states",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastSyncAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    GroupId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    GroupName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Candidates = table.Column<int>(type: "integer", nullable: false),
                    ExcludedUnsubscribed = table.Column<int>(type: "integer", nullable: false),
                    Created = table.Column<int>(type: "integer", nullable: false),
                    Assigned = table.Column<int>(type: "integer", nullable: false),
                    AlreadyAssigned = table.Column<int>(type: "integer", nullable: false),
                    Unassigned = table.Column<int>(type: "integer", nullable: false),
                    Errors = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mailerlite_sync_states", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mailerlite_sync_states");
        }
    }
}
