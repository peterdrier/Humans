using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEventGuide : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "event_categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Slug = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    IsSensitive = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "guide_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventSettingsId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionOpenAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    SubmissionCloseAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    GuidePublishAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    MaxPrintSlots = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guide_settings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_guide_settings_event_settings_EventSettingsId",
                        column: x => x.EventSettingsId,
                        principalTable: "event_settings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guide_shared_venues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LocationDescription = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guide_shared_venues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "user_guide_preferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExcludedCategorySlugs = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_guide_preferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_guide_preferences_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guide_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampId = table.Column<Guid>(type: "uuid", nullable: true),
                    GuideSharedVenueId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubmitterUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    LocationNote = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    StartAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    IsRecurring = table.Column<bool>(type: "boolean", nullable: false),
                    RecurrenceDays = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PriorityRank = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AdminNotes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SubmittedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LastUpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guide_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_guide_events_camps_CampId",
                        column: x => x.CampId,
                        principalTable: "camps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_guide_events_event_categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "event_categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_guide_events_guide_shared_venues_GuideSharedVenueId",
                        column: x => x.GuideSharedVenueId,
                        principalTable: "guide_shared_venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_guide_events_users_SubmitterUserId",
                        column: x => x.SubmitterUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "moderation_actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GuideEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_moderation_actions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_moderation_actions_guide_events_GuideEventId",
                        column: x => x.GuideEventId,
                        principalTable: "guide_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_moderation_actions_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_event_favourites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuideEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_event_favourites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_event_favourites_guide_events_GuideEventId",
                        column: x => x.GuideEventId,
                        principalTable: "guide_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_event_favourites_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_event_categories_IsActive",
                table: "event_categories",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_event_categories_Slug",
                table: "event_categories",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_guide_events_CampId",
                table: "guide_events",
                column: "CampId");

            migrationBuilder.CreateIndex(
                name: "IX_guide_events_CategoryId",
                table: "guide_events",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_guide_events_GuideSharedVenueId",
                table: "guide_events",
                column: "GuideSharedVenueId");

            migrationBuilder.CreateIndex(
                name: "IX_guide_events_Status",
                table: "guide_events",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_guide_events_SubmitterUserId",
                table: "guide_events",
                column: "SubmitterUserId");

            migrationBuilder.CreateIndex(
                name: "IX_guide_settings_EventSettingsId",
                table: "guide_settings",
                column: "EventSettingsId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_guide_shared_venues_IsActive",
                table: "guide_shared_venues",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_moderation_actions_ActorUserId",
                table: "moderation_actions",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_moderation_actions_GuideEventId",
                table: "moderation_actions",
                column: "GuideEventId");

            migrationBuilder.CreateIndex(
                name: "IX_user_event_favourites_GuideEventId",
                table: "user_event_favourites",
                column: "GuideEventId");

            migrationBuilder.CreateIndex(
                name: "IX_user_event_favourites_UserId_GuideEventId",
                table: "user_event_favourites",
                columns: new[] { "UserId", "GuideEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_guide_preferences_UserId",
                table: "user_guide_preferences",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guide_settings");

            migrationBuilder.DropTable(
                name: "moderation_actions");

            migrationBuilder.DropTable(
                name: "user_event_favourites");

            migrationBuilder.DropTable(
                name: "user_guide_preferences");

            migrationBuilder.DropTable(
                name: "guide_events");

            migrationBuilder.DropTable(
                name: "event_categories");

            migrationBuilder.DropTable(
                name: "guide_shared_venues");
        }
    }
}
