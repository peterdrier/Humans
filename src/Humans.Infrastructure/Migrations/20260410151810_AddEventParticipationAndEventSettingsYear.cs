using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEventParticipationAndEventSettingsYear : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Year",
                table: "event_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill Year from GateOpeningDate so the feature isn't inert after deploy
            migrationBuilder.Sql(
                @"UPDATE event_settings SET ""Year"" = EXTRACT(YEAR FROM ""GateOpeningDate"")::integer WHERE ""GateOpeningDate"" IS NOT NULL;");

            migrationBuilder.CreateTable(
                name: "event_participations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DeclaredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_participations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_event_participations_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_event_participations_UserId_Year",
                table: "event_participations",
                columns: new[] { "UserId", "Year" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_participations");

            migrationBuilder.DropColumn(
                name: "Year",
                table: "event_settings");
        }
    }
}
