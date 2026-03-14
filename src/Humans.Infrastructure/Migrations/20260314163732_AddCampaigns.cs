using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UnsubscribedFromCampaigns",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "campaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    EmailSubject = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    EmailBodyTemplate = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaigns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_campaigns_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "campaign_codes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ImportedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaign_codes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_campaign_codes_campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "campaign_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignCodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LatestEmailStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LatestEmailAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaign_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_campaign_grants_campaign_codes_CampaignCodeId",
                        column: x => x.CampaignCodeId,
                        principalTable: "campaign_codes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_campaign_grants_campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_campaign_grants_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_campaign_codes_CampaignId_Code",
                table: "campaign_codes",
                columns: new[] { "CampaignId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_campaign_grants_CampaignCodeId",
                table: "campaign_grants",
                column: "CampaignCodeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_campaign_grants_CampaignId_UserId",
                table: "campaign_grants",
                columns: new[] { "CampaignId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_campaign_grants_UserId",
                table: "campaign_grants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_CreatedByUserId",
                table: "campaigns",
                column: "CreatedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_email_outbox_messages_campaign_grants_CampaignGrantId",
                table: "email_outbox_messages",
                column: "CampaignGrantId",
                principalTable: "campaign_grants",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_email_outbox_messages_campaign_grants_CampaignGrantId",
                table: "email_outbox_messages");

            migrationBuilder.DropTable(
                name: "campaign_grants");

            migrationBuilder.DropTable(
                name: "campaign_codes");

            migrationBuilder.DropTable(
                name: "campaigns");

            migrationBuilder.DropColumn(
                name: "UnsubscribedFromCampaigns",
                table: "users");
        }
    }
}
