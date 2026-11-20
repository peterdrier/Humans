using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Governance.Data.Migrations
{
    /// <inheritdoc />
    public partial class GovernanceBodyLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "governance_body_links",
                columns: table => new
                {
                    Body = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    GoogleGroupPrefix = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_governance_body_links", x => x.Body);
                });

            migrationBuilder.CreateTable(
                name: "governance_body_files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Body = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    GoogleId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    PermissionLevel = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_governance_body_files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_governance_body_files_governance_body_links_Body",
                        column: x => x.Body,
                        principalTable: "governance_body_links",
                        principalColumn: "Body",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_governance_body_files_Body_GoogleId",
                table: "governance_body_files",
                columns: new[] { "Body", "GoogleId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "governance_body_files");

            migrationBuilder.DropTable(
                name: "governance_body_links");
        }
    }
}
