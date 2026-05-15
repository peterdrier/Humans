using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEventsSection : Migration
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
                name: "event_guide_settings",
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
                    table.PrimaryKey("PK_event_guide_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "event_preferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExcludedCategorySlugs = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_preferences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "event_venues",
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
                    table.PrimaryKey("PK_event_venues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "events",
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
                    table.PrimaryKey("PK_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_events_event_categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "event_categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_events_event_venues_GuideSharedVenueId",
                        column: x => x.GuideSharedVenueId,
                        principalTable: "event_venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "event_favourites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuideEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_favourites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_event_favourites_events_GuideEventId",
                        column: x => x.GuideEventId,
                        principalTable: "events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "event_moderation_actions",
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
                    table.PrimaryKey("PK_event_moderation_actions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_event_moderation_actions_events_GuideEventId",
                        column: x => x.GuideEventId,
                        principalTable: "events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "event_categories",
                columns: new[] { "Id", "DisplayOrder", "IsActive", "IsSensitive", "Name", "Slug" },
                values: new object[,]
                {
                    { new Guid("00000000-0000-0000-0026-000000000001"), 1, true, false, "Workshop", "workshop" },
                    { new Guid("00000000-0000-0000-0026-000000000002"), 2, true, false, "Party", "party" },
                    { new Guid("00000000-0000-0000-0026-000000000003"), 3, true, false, "Food and drink", "food-and-drink" },
                    { new Guid("00000000-0000-0000-0026-000000000004"), 4, true, false, "Chillout", "chillout" },
                    { new Guid("00000000-0000-0000-0026-000000000005"), 5, true, true, "Spiritual / Healing", "spiritual-healing" },
                    { new Guid("00000000-0000-0000-0026-000000000006"), 8, true, false, "Other", "other" },
                    { new Guid("00000000-0000-0000-0026-000000000007"), 6, true, true, "Adults", "adults" },
                    { new Guid("00000000-0000-0000-0026-000000000008"), 7, true, false, "Kids", "kids" }
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
                name: "IX_event_favourites_GuideEventId",
                table: "event_favourites",
                column: "GuideEventId");

            migrationBuilder.CreateIndex(
                name: "IX_event_favourites_UserId_GuideEventId",
                table: "event_favourites",
                columns: new[] { "UserId", "GuideEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_event_guide_settings_EventSettingsId",
                table: "event_guide_settings",
                column: "EventSettingsId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_event_moderation_actions_GuideEventId",
                table: "event_moderation_actions",
                column: "GuideEventId");

            migrationBuilder.CreateIndex(
                name: "IX_event_preferences_UserId",
                table: "event_preferences",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_event_venues_IsActive",
                table: "event_venues",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_events_CampId",
                table: "events",
                column: "CampId");

            migrationBuilder.CreateIndex(
                name: "IX_events_CategoryId",
                table: "events",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_events_GuideSharedVenueId",
                table: "events",
                column: "GuideSharedVenueId");

            migrationBuilder.CreateIndex(
                name: "IX_events_Status",
                table: "events",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_events_SubmitterUserId",
                table: "events",
                column: "SubmitterUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_favourites");

            migrationBuilder.DropTable(
                name: "event_guide_settings");

            migrationBuilder.DropTable(
                name: "event_moderation_actions");

            migrationBuilder.DropTable(
                name: "event_preferences");

            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "event_categories");

            migrationBuilder.DropTable(
                name: "event_venues");
        }
    }
}
