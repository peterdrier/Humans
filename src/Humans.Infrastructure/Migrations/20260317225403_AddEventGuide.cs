using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEventGuide : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_google_resources_teams_TeamId",
                table: "google_resources");

            migrationBuilder.DropColumn(
                name: "ContactMethod",
                table: "camps");

            migrationBuilder.DropColumn(
                name: "ActorName",
                table: "audit_log");

            migrationBuilder.AddColumn<string>(
                name: "AllergyOtherText",
                table: "volunteer_event_profiles",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntoleranceOtherText",
                table: "volunteer_event_profiles",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactSource",
                table: "users",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "DeletionEligibleAfter",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSourceId",
                table: "users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoogleEmail",
                table: "users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoogleEmailStatus",
                table: "users",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<Instant>(
                name: "MagicLinkSentAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ApplicationFee",
                table: "ticket_orders",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountAmount",
                table: "ticket_orders",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DonationAmount",
                table: "ticket_orders",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethod",
                table: "ticket_orders",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethodDetail",
                table: "ticket_orders",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StripeFee",
                table: "ticket_orders",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripePaymentIntentId",
                table: "ticket_orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VatAmount",
                table: "ticket_orders",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "CallsToAction",
                table: "teams",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomSlug",
                table: "teams",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasBudget",
                table: "teams",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsHidden",
                table: "teams",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPromotedToDirectory",
                table: "teams",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPublicPage",
                table: "teams",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSensitive",
                table: "teams",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PageContent",
                table: "teams",
                type: "character varying(50000)",
                maxLength: 50000,
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "PageContentUpdatedAt",
                table: "teams",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PageContentUpdatedByUserId",
                table: "teams",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowCoordinatorsOnPublicPage",
                table: "teams",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPublic",
                table: "team_role_definitions",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AlterColumn<bool>(
                name: "IsAllDay",
                table: "shifts",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsVisibleToVolunteers",
                table: "rotas",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AlterColumn<bool>(
                name: "NoPriorBurnExperience",
                table: "profiles",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: false);

            migrationBuilder.AlterColumn<bool>(
                name: "IsApproved",
                table: "profiles",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: false);

            migrationBuilder.AlterColumn<int>(
                name: "RetryCount",
                table: "google_sync_outbox",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "FailedPermanently",
                table: "google_sync_outbox",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DrivePermissionLevel",
                table: "google_resources",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "RestrictInheritedAccess",
                table: "google_resources",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Year",
                table: "event_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "HideHistoricalNames",
                table: "camps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Links",
                table: "camps",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "account_merge_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PendingEmailId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AdminNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_merge_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_account_merge_requests_users_ResolvedByUserId",
                        column: x => x.ResolvedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_account_merge_requests_users_SourceUserId",
                        column: x => x.SourceUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_account_merge_requests_users_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "budget_years",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_years", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "calendar_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Location = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LocationUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    OwningTeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartUtc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    IsAllDay = table.Column<bool>(type: "boolean", nullable: false),
                    RecurrenceRule = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RecurrenceTimezone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RecurrenceUntilUtc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendar_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_calendar_events_teams_OwningTeamId",
                        column: x => x.OwningTeamId,
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "camp_members",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampSeasonId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RequestedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ConfirmedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RemovedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    RemovedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_members", x => x.Id);
                    table.ForeignKey(
                        name: "FK_camp_members_camp_seasons_CampSeasonId",
                        column: x => x.CampSeasonId,
                        principalTable: "camp_seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "camp_polygon_histories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampSeasonId = table.Column<Guid>(type: "uuid", nullable: false),
                    GeoJson = table.Column<string>(type: "text", nullable: false),
                    AreaSqm = table.Column<double>(type: "double precision", nullable: false),
                    ModifiedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModifiedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_polygon_histories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_camp_polygon_histories_camp_seasons_CampSeasonId",
                        column: x => x.CampSeasonId,
                        principalTable: "camp_seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_camp_polygon_histories_users_ModifiedByUserId",
                        column: x => x.ModifiedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "camp_polygons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampSeasonId = table.Column<Guid>(type: "uuid", nullable: false),
                    GeoJson = table.Column<string>(type: "text", nullable: false),
                    AreaSqm = table.Column<double>(type: "double precision", nullable: false),
                    LastModifiedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastModifiedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_polygons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_camp_polygons_camp_seasons_CampSeasonId",
                        column: x => x.CampSeasonId,
                        principalTable: "camp_seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_camp_polygons_users_LastModifiedByUserId",
                        column: x => x.LastModifiedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "camp_role_definitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SlotCount = table.Column<int>(type: "integer", nullable: false),
                    MinimumRequired = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    DeactivatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_role_definitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "city_planning_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    IsPlacementOpen = table.Column<bool>(type: "boolean", nullable: false),
                    OpenedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    PlacementOpensAt = table.Column<LocalDateTime>(type: "timestamp without time zone", nullable: true),
                    PlacementClosesAt = table.Column<LocalDateTime>(type: "timestamp without time zone", nullable: true),
                    RegistrationInfo = table.Column<string>(type: "text", nullable: true),
                    LimitZoneGeoJson = table.Column<string>(type: "text", nullable: true),
                    OfficialZonesGeoJson = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_city_planning_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "communication_preferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OptedOut = table.Column<bool>(type: "boolean", nullable: false),
                    InboxEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdateSource = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_communication_preferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_communication_preferences_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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

            migrationBuilder.CreateTable(
                name: "feedback_reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    PageUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    UserAgent = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AdditionalContext = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ScreenshotFileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ScreenshotStoragePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ScreenshotContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    GitHubIssueNumber = table.Column<int>(type: "integer", nullable: true),
                    LastReporterMessageAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastAdminMessageAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedToUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedToTeamId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feedback_reports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_feedback_reports_teams_AssignedToTeamId",
                        column: x => x.AssignedToTeamId,
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_feedback_reports_users_AssignedToUserId",
                        column: x => x.AssignedToUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_feedback_reports_users_ResolvedByUserId",
                        column: x => x.ResolvedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_feedback_reports_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                name: "notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ActionUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ActionLabel = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Priority = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Class = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TargetGroupName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notifications_users_ResolvedByUserId",
                        column: x => x.ResolvedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "profile_languages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    LanguageCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Proficiency = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_languages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_profile_languages_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shift_tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shift_tags", x => x.Id);
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
                name: "budget_audit_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetYearId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    OldValue = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    NewValue = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_audit_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_budget_audit_logs_budget_years_BudgetYearId",
                        column: x => x.BudgetYearId,
                        principalTable: "budget_years",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_budget_audit_logs_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "budget_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetYearId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsRestricted = table.Column<bool>(type: "boolean", nullable: false),
                    IsDepartmentGroup = table.Column<bool>(type: "boolean", nullable: false),
                    IsTicketingGroup = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_budget_groups_budget_years_BudgetYearId",
                        column: x => x.BudgetYearId,
                        principalTable: "budget_years",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "calendar_event_exceptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalOccurrenceStartUtc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    IsCancelled = table.Column<bool>(type: "boolean", nullable: false),
                    OverrideStartUtc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    OverrideEndUtc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    OverrideTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OverrideDescription = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    OverrideLocation = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OverrideLocationUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendar_event_exceptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_calendar_event_exceptions_calendar_events_EventId",
                        column: x => x.EventId,
                        principalTable: "calendar_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "camp_role_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampSeasonId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampRoleDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    AssignedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_role_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_camp_role_assignments_camp_members_CampMemberId",
                        column: x => x.CampMemberId,
                        principalTable: "camp_members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_camp_role_assignments_camp_role_definitions_CampRoleDefinit~",
                        column: x => x.CampRoleDefinitionId,
                        principalTable: "camp_role_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_camp_role_assignments_camp_seasons_CampSeasonId",
                        column: x => x.CampSeasonId,
                        principalTable: "camp_seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "feedback_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FeedbackReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Content = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feedback_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_feedback_messages_feedback_reports_FeedbackReportId",
                        column: x => x.FeedbackReportId,
                        principalTable: "feedback_reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_feedback_messages_users_SenderUserId",
                        column: x => x.SenderUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
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
                name: "notification_recipients",
                columns: table => new
                {
                    NotificationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReadAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_recipients", x => new { x.NotificationId, x.UserId });
                    table.ForeignKey(
                        name: "FK_notification_recipients_notifications_NotificationId",
                        column: x => x.NotificationId,
                        principalTable: "notifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notification_recipients_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rota_shift_tags",
                columns: table => new
                {
                    RotaId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShiftTagId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rota_shift_tags", x => new { x.RotaId, x.ShiftTagId });
                    table.ForeignKey(
                        name: "FK_rota_shift_tags_rotas_RotaId",
                        column: x => x.RotaId,
                        principalTable: "rotas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_rota_shift_tags_shift_tags_ShiftTagId",
                        column: x => x.ShiftTagId,
                        principalTable: "shift_tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "volunteer_tag_preferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShiftTagId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volunteer_tag_preferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_volunteer_tag_preferences_shift_tags_ShiftTagId",
                        column: x => x.ShiftTagId,
                        principalTable: "shift_tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_volunteer_tag_preferences_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "budget_categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AllocatedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ExpenditureType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_categories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_budget_categories_budget_groups_BudgetGroupId",
                        column: x => x.BudgetGroupId,
                        principalTable: "budget_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_budget_categories_teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ticketing_projections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartDate = table.Column<LocalDate>(type: "date", nullable: true),
                    EventDate = table.Column<LocalDate>(type: "date", nullable: true),
                    InitialSalesCount = table.Column<int>(type: "integer", nullable: false),
                    DailySalesRate = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AverageTicketPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    VatRate = table.Column<int>(type: "integer", nullable: false),
                    StripeFeePercent = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    StripeFeeFixed = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TicketTailorFeePercent = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticketing_projections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ticketing_projections_budget_groups_BudgetGroupId",
                        column: x => x.BudgetGroupId,
                        principalTable: "budget_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                        onDelete: ReferentialAction.Restrict);
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

            migrationBuilder.CreateTable(
                name: "budget_line_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetCategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ResponsibleTeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ExpectedDate = table.Column<LocalDate>(type: "date", nullable: true),
                    VatRate = table.Column<int>(type: "integer", nullable: false),
                    IsAutoGenerated = table.Column<bool>(type: "boolean", nullable: false),
                    IsCashflowOnly = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_line_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_budget_line_items_budget_categories_BudgetCategoryId",
                        column: x => x.BudgetCategoryId,
                        principalTable: "budget_categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_budget_line_items_teams_ResponsibleTeamId",
                        column: x => x.ResponsibleTeamId,
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
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
                    { new Guid("00000000-0000-0000-0026-000000000006"), 6, true, false, "Other", "other" }
                });

            migrationBuilder.InsertData(
                table: "shift_tags",
                columns: new[] { "Id", "Name" },
                values: new object[,]
                {
                    { new Guid("00000000-0000-0000-0003-000000000001"), "Heavy lifting" },
                    { new Guid("00000000-0000-0000-0003-000000000002"), "Working in the sun" },
                    { new Guid("00000000-0000-0000-0003-000000000003"), "Working in the shade" },
                    { new Guid("00000000-0000-0000-0003-000000000004"), "Organisational task" },
                    { new Guid("00000000-0000-0000-0003-000000000005"), "Meeting new people" },
                    { new Guid("00000000-0000-0000-0003-000000000006"), "Looking after folks" },
                    { new Guid("00000000-0000-0000-0003-000000000007"), "Exploring the site" },
                    { new Guid("00000000-0000-0000-0003-000000000008"), "Feeding and hydrating folks" }
                });

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000001"),
                columns: new[] { "CallsToAction", "CustomSlug", "HasBudget", "IsHidden", "IsPromotedToDirectory", "IsPublicPage", "IsSensitive", "PageContent", "PageContentUpdatedAt", "PageContentUpdatedByUserId", "ShowCoordinatorsOnPublicPage" },
                values: new object[] { null, null, false, false, false, false, false, null, null, null, true });

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000002"),
                columns: new[] { "CallsToAction", "CustomSlug", "HasBudget", "IsHidden", "IsPromotedToDirectory", "IsPublicPage", "IsSensitive", "PageContent", "PageContentUpdatedAt", "PageContentUpdatedByUserId", "ShowCoordinatorsOnPublicPage" },
                values: new object[] { null, null, false, false, false, false, false, null, null, null, true });

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000003"),
                columns: new[] { "CallsToAction", "CustomSlug", "HasBudget", "IsHidden", "IsPromotedToDirectory", "IsPublicPage", "IsSensitive", "PageContent", "PageContentUpdatedAt", "PageContentUpdatedByUserId", "ShowCoordinatorsOnPublicPage" },
                values: new object[] { null, null, false, false, false, false, false, null, null, null, true });

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000004"),
                columns: new[] { "CallsToAction", "CustomSlug", "HasBudget", "IsHidden", "IsPromotedToDirectory", "IsPublicPage", "IsSensitive", "PageContent", "PageContentUpdatedAt", "PageContentUpdatedByUserId", "ShowCoordinatorsOnPublicPage" },
                values: new object[] { null, null, false, false, false, false, false, null, null, null, true });

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000005"),
                columns: new[] { "CallsToAction", "CustomSlug", "HasBudget", "IsHidden", "IsPromotedToDirectory", "IsPublicPage", "IsSensitive", "PageContent", "PageContentUpdatedAt", "PageContentUpdatedByUserId", "ShowCoordinatorsOnPublicPage" },
                values: new object[] { null, null, false, false, false, false, false, null, null, null, true });

            migrationBuilder.InsertData(
                table: "teams",
                columns: new[] { "Id", "CallsToAction", "CreatedAt", "CustomSlug", "Description", "GoogleGroupPrefix", "HasBudget", "IsActive", "IsHidden", "IsPromotedToDirectory", "IsPublicPage", "IsSensitive", "Name", "PageContent", "PageContentUpdatedAt", "PageContentUpdatedByUserId", "ParentTeamId", "ShowCoordinatorsOnPublicPage", "Slug", "SystemTeamType", "UpdatedAt" },
                values: new object[] { new Guid("00000000-0000-0000-0001-000000000006"), null, NodaTime.Instant.FromUnixTimeTicks(17702491570000000L), null, "All active camp leads across all camps", null, false, true, false, false, false, false, "Barrio Leads", null, null, null, null, true, "barrio-leads", "BarrioLeads", NodaTime.Instant.FromUnixTimeTicks(17702491570000000L) });

            migrationBuilder.CreateIndex(
                name: "IX_users_ContactSource_ExternalSourceId",
                table: "users",
                columns: new[] { "ContactSource", "ExternalSourceId" },
                filter: "\"ExternalSourceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_orders_PaymentMethod",
                table: "ticket_orders",
                column: "PaymentMethod");

            migrationBuilder.CreateIndex(
                name: "IX_teams_CustomSlug",
                table: "teams",
                column: "CustomSlug",
                unique: true,
                filter: "\"CustomSlug\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_account_merge_requests_ResolvedByUserId",
                table: "account_merge_requests",
                column: "ResolvedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_account_merge_requests_SourceUserId",
                table: "account_merge_requests",
                column: "SourceUserId");

            migrationBuilder.CreateIndex(
                name: "IX_account_merge_requests_Status",
                table: "account_merge_requests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_account_merge_requests_TargetUserId",
                table: "account_merge_requests",
                column: "TargetUserId");

            migrationBuilder.CreateIndex(
                name: "IX_budget_audit_logs_ActorUserId",
                table: "budget_audit_logs",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_budget_audit_logs_BudgetYearId",
                table: "budget_audit_logs",
                column: "BudgetYearId");

            migrationBuilder.CreateIndex(
                name: "IX_budget_audit_logs_EntityType_EntityId",
                table: "budget_audit_logs",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_budget_audit_logs_OccurredAt",
                table: "budget_audit_logs",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_budget_categories_BudgetGroupId_SortOrder",
                table: "budget_categories",
                columns: new[] { "BudgetGroupId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_budget_categories_TeamId",
                table: "budget_categories",
                column: "TeamId",
                filter: "\"TeamId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_budget_groups_BudgetYearId_SortOrder",
                table: "budget_groups",
                columns: new[] { "BudgetYearId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_budget_line_items_BudgetCategoryId_SortOrder",
                table: "budget_line_items",
                columns: new[] { "BudgetCategoryId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_budget_line_items_ResponsibleTeamId",
                table: "budget_line_items",
                column: "ResponsibleTeamId",
                filter: "\"ResponsibleTeamId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_budget_years_Status",
                table: "budget_years",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_budget_years_Year",
                table: "budget_years",
                column: "Year",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_calendar_event_exceptions_EventId_OriginalOccurrenceStartUtc",
                table: "calendar_event_exceptions",
                columns: new[] { "EventId", "OriginalOccurrenceStartUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_calendar_events_OwningTeamId_StartUtc",
                table: "calendar_events",
                columns: new[] { "OwningTeamId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_calendar_events_StartUtc_RecurrenceUntilUtc",
                table: "calendar_events",
                columns: new[] { "StartUtc", "RecurrenceUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_camp_members_active_unique",
                table: "camp_members",
                columns: new[] { "CampSeasonId", "UserId" },
                unique: true,
                filter: "\"Status\" <> 'Removed'");

            migrationBuilder.CreateIndex(
                name: "IX_camp_members_UserId",
                table: "camp_members",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_polygon_histories_CampSeasonId_ModifiedAt",
                table: "camp_polygon_histories",
                columns: new[] { "CampSeasonId", "ModifiedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_camp_polygon_histories_ModifiedByUserId",
                table: "camp_polygon_histories",
                column: "ModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_polygons_CampSeasonId",
                table: "camp_polygons",
                column: "CampSeasonId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_camp_polygons_LastModifiedByUserId",
                table: "camp_polygons",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_role_assignments_CampMemberId",
                table: "camp_role_assignments",
                column: "CampMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_role_assignments_CampRoleDefinitionId",
                table: "camp_role_assignments",
                column: "CampRoleDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_role_assignments_unique",
                table: "camp_role_assignments",
                columns: new[] { "CampSeasonId", "CampRoleDefinitionId", "CampMemberId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_camp_role_definitions_name_unique",
                table: "camp_role_definitions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_camp_role_definitions_SortOrder",
                table: "camp_role_definitions",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_city_planning_settings_Year",
                table: "city_planning_settings",
                column: "Year",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_communication_preferences_UserId",
                table: "communication_preferences",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_communication_preferences_UserId_Category",
                table: "communication_preferences",
                columns: new[] { "UserId", "Category" },
                unique: true);

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
                name: "IX_event_participations_UserId_Year",
                table: "event_participations",
                columns: new[] { "UserId", "Year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_feedback_messages_CreatedAt",
                table: "feedback_messages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_messages_FeedbackReportId",
                table: "feedback_messages",
                column: "FeedbackReportId");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_messages_SenderUserId",
                table: "feedback_messages",
                column: "SenderUserId");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_AssignedToTeamId",
                table: "feedback_reports",
                column: "AssignedToTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_AssignedToUserId",
                table: "feedback_reports",
                column: "AssignedToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_CreatedAt",
                table: "feedback_reports",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_ResolvedByUserId",
                table: "feedback_reports",
                column: "ResolvedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_Status",
                table: "feedback_reports",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_UserId",
                table: "feedback_reports",
                column: "UserId");

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
                name: "IX_NotificationRecipient_UserId",
                table: "notification_recipients",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_CreatedAt",
                table: "notifications",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_ResolvedByUserId",
                table: "notifications",
                column: "ResolvedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_profile_languages_ProfileId",
                table: "profile_languages",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_rota_shift_tags_ShiftTagId",
                table: "rota_shift_tags",
                column: "ShiftTagId");

            migrationBuilder.CreateIndex(
                name: "IX_shift_tags_name_unique",
                table: "shift_tags",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticketing_projections_BudgetGroupId",
                table: "ticketing_projections",
                column: "BudgetGroupId",
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_tag_preferences_ShiftTagId",
                table: "volunteer_tag_preferences",
                column: "ShiftTagId");

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_tag_preferences_user_tag_unique",
                table: "volunteer_tag_preferences",
                columns: new[] { "UserId", "ShiftTagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_tag_preferences_UserId",
                table: "volunteer_tag_preferences",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_google_resources_teams_TeamId",
                table: "google_resources",
                column: "TeamId",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_google_resources_teams_TeamId",
                table: "google_resources");

            migrationBuilder.DropTable(
                name: "account_merge_requests");

            migrationBuilder.DropTable(
                name: "budget_audit_logs");

            migrationBuilder.DropTable(
                name: "budget_line_items");

            migrationBuilder.DropTable(
                name: "calendar_event_exceptions");

            migrationBuilder.DropTable(
                name: "camp_polygon_histories");

            migrationBuilder.DropTable(
                name: "camp_polygons");

            migrationBuilder.DropTable(
                name: "camp_role_assignments");

            migrationBuilder.DropTable(
                name: "city_planning_settings");

            migrationBuilder.DropTable(
                name: "communication_preferences");

            migrationBuilder.DropTable(
                name: "event_participations");

            migrationBuilder.DropTable(
                name: "feedback_messages");

            migrationBuilder.DropTable(
                name: "guide_settings");

            migrationBuilder.DropTable(
                name: "moderation_actions");

            migrationBuilder.DropTable(
                name: "notification_recipients");

            migrationBuilder.DropTable(
                name: "profile_languages");

            migrationBuilder.DropTable(
                name: "rota_shift_tags");

            migrationBuilder.DropTable(
                name: "ticketing_projections");

            migrationBuilder.DropTable(
                name: "user_event_favourites");

            migrationBuilder.DropTable(
                name: "user_guide_preferences");

            migrationBuilder.DropTable(
                name: "volunteer_tag_preferences");

            migrationBuilder.DropTable(
                name: "budget_categories");

            migrationBuilder.DropTable(
                name: "calendar_events");

            migrationBuilder.DropTable(
                name: "camp_members");

            migrationBuilder.DropTable(
                name: "camp_role_definitions");

            migrationBuilder.DropTable(
                name: "feedback_reports");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "guide_events");

            migrationBuilder.DropTable(
                name: "shift_tags");

            migrationBuilder.DropTable(
                name: "budget_groups");

            migrationBuilder.DropTable(
                name: "event_categories");

            migrationBuilder.DropTable(
                name: "guide_shared_venues");

            migrationBuilder.DropTable(
                name: "budget_years");

            migrationBuilder.DropIndex(
                name: "IX_users_ContactSource_ExternalSourceId",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_ticket_orders_PaymentMethod",
                table: "ticket_orders");

            migrationBuilder.DropIndex(
                name: "IX_teams_CustomSlug",
                table: "teams");

            migrationBuilder.DeleteData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000006"));

            migrationBuilder.DropColumn(
                name: "AllergyOtherText",
                table: "volunteer_event_profiles");

            migrationBuilder.DropColumn(
                name: "IntoleranceOtherText",
                table: "volunteer_event_profiles");

            migrationBuilder.DropColumn(
                name: "ContactSource",
                table: "users");

            migrationBuilder.DropColumn(
                name: "DeletionEligibleAfter",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ExternalSourceId",
                table: "users");

            migrationBuilder.DropColumn(
                name: "GoogleEmail",
                table: "users");

            migrationBuilder.DropColumn(
                name: "GoogleEmailStatus",
                table: "users");

            migrationBuilder.DropColumn(
                name: "MagicLinkSentAt",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ApplicationFee",
                table: "ticket_orders");

            migrationBuilder.DropColumn(
                name: "DiscountAmount",
                table: "ticket_orders");

            migrationBuilder.DropColumn(
                name: "DonationAmount",
                table: "ticket_orders");

            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                table: "ticket_orders");

            migrationBuilder.DropColumn(
                name: "PaymentMethodDetail",
                table: "ticket_orders");

            migrationBuilder.DropColumn(
                name: "StripeFee",
                table: "ticket_orders");

            migrationBuilder.DropColumn(
                name: "StripePaymentIntentId",
                table: "ticket_orders");

            migrationBuilder.DropColumn(
                name: "VatAmount",
                table: "ticket_orders");

            migrationBuilder.DropColumn(
                name: "CallsToAction",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "CustomSlug",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "HasBudget",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "IsHidden",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "IsPromotedToDirectory",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "IsPublicPage",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "IsSensitive",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "PageContent",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "PageContentUpdatedAt",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "PageContentUpdatedByUserId",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "ShowCoordinatorsOnPublicPage",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "IsPublic",
                table: "team_role_definitions");

            migrationBuilder.DropColumn(
                name: "IsVisibleToVolunteers",
                table: "rotas");

            migrationBuilder.DropColumn(
                name: "FailedPermanently",
                table: "google_sync_outbox");

            migrationBuilder.DropColumn(
                name: "DrivePermissionLevel",
                table: "google_resources");

            migrationBuilder.DropColumn(
                name: "RestrictInheritedAccess",
                table: "google_resources");

            migrationBuilder.DropColumn(
                name: "Year",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "HideHistoricalNames",
                table: "camps");

            migrationBuilder.DropColumn(
                name: "Links",
                table: "camps");

            migrationBuilder.AlterColumn<bool>(
                name: "IsAllDay",
                table: "shifts",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AlterColumn<bool>(
                name: "NoPriorBurnExperience",
                table: "profiles",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AlterColumn<bool>(
                name: "IsApproved",
                table: "profiles",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AlterColumn<int>(
                name: "RetryCount",
                table: "google_sync_outbox",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "ContactMethod",
                table: "camps",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ActorName",
                table: "audit_log",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddForeignKey(
                name: "FK_google_resources_teams_TeamId",
                table: "google_resources",
                column: "TeamId",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
