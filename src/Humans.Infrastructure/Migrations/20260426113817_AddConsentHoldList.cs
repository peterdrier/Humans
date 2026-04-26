using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConsentHoldList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "consent_hold_list",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Entry = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AddedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consent_hold_list", x => x.Id);
                    table.ForeignKey(
                        name: "FK_consent_hold_list_users_AddedByUserId",
                        column: x => x.AddedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "sync_service_settings",
                columns: new[] { "Id", "ServiceType", "SyncMode", "UpdatedAt", "UpdatedByUserId" },
                values: new object[] { new Guid("00000000-0000-0000-0002-000000000004"), "AutoConsentCheck", "None", NodaTime.Instant.FromUnixTimeTicks(17730144000000000L), null });

            migrationBuilder.CreateIndex(
                name: "IX_consent_hold_list_AddedByUserId",
                table: "consent_hold_list",
                column: "AddedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consent_hold_list");

            migrationBuilder.DeleteData(
                table: "sync_service_settings",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0002-000000000004"));
        }
    }
}
