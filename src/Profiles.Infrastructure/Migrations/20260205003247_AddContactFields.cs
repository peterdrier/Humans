using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Profiles.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContactFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contact_fields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CustomLabel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Visibility = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contact_fields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_contact_fields_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_contact_fields_ProfileId",
                table: "contact_fields",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_contact_fields_ProfileId_Visibility",
                table: "contact_fields",
                columns: new[] { "ProfileId", "Visibility" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contact_fields");
        }
    }
}
