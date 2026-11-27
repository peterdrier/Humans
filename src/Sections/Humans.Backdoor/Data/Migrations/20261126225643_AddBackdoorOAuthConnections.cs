using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Backdoor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBackdoorOAuthConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backdoor_oauth_connections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Scopes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Properties = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backdoor_oauth_connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "backdoor_oauth_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClientId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    RedeemedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ReferenceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Payload = table.Column<string>(type: "text", nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backdoor_oauth_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_backdoor_oauth_tokens_backdoor_oauth_connections_Connection~",
                        column: x => x.ConnectionId,
                        principalTable: "backdoor_oauth_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_backdoor_oauth_connections_UserId",
                table: "backdoor_oauth_connections",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_backdoor_oauth_tokens_ConnectionId",
                table: "backdoor_oauth_tokens",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_backdoor_oauth_tokens_ReferenceId",
                table: "backdoor_oauth_tokens",
                column: "ReferenceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backdoor_oauth_tokens_UserId",
                table: "backdoor_oauth_tokens",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backdoor_oauth_tokens");

            migrationBuilder.DropTable(
                name: "backdoor_oauth_connections");
        }
    }
}
