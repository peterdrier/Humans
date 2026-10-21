using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Grants.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "grant_programs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Entitles = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    IssuedBy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    GrantsExpireOn = table.Column<LocalDate>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grant_programs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "grant_program_managers",
                columns: table => new
                {
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grant_program_managers", x => new { x.ProgramId, x.UserId });
                    table.ForeignKey(
                        name: "FK_grant_program_managers_grant_programs_ProgramId",
                        column: x => x.ProgramId,
                        principalTable: "grant_programs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRef = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ExpiresOn = table.Column<LocalDate>(type: "date", nullable: true),
                    IssuedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    IssuedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    NotifiedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokeReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_grants_grant_programs_ProgramId",
                        column: x => x.ProgramId,
                        principalTable: "grant_programs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_grant_program_managers_UserId",
                table: "grant_program_managers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_grants_ProgramId_UserId",
                table: "grants",
                columns: new[] { "ProgramId", "UserId" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_grants_UserId",
                table: "grants",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "grant_program_managers");

            migrationBuilder.DropTable(
                name: "grants");

            migrationBuilder.DropTable(
                name: "grant_programs");
        }
    }
}
