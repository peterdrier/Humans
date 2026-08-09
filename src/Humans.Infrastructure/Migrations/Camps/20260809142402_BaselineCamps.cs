using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations.Camps
{
    /// <inheritdoc />
    public partial class BaselineCamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "camp_role_definitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Slug = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SlotCount = table.Column<int>(type: "integer", nullable: false),
                    MinimumRequired = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    DeactivatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    SpecialRole = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_role_definitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "camp_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicYear = table.Column<int>(type: "integer", nullable: false),
                    OpenSeasons = table.Column<string>(type: "jsonb", nullable: false),
                    EeStartDate = table.Column<LocalDate>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "camps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ContactEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ContactPhone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WebOrSocialUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Links = table.Column<string>(type: "jsonb", nullable: true),
                    IsSwissCamp = table.Column<bool>(type: "boolean", nullable: false),
                    HideHistoricalNames = table.Column<bool>(type: "boolean", nullable: false),
                    TimesAtNowhere = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "camp_historical_names",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: true),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_historical_names", x => x.Id);
                    table.ForeignKey(
                        name: "FK_camp_historical_names_camps_CampId",
                        column: x => x.CampId,
                        principalTable: "camps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "camp_images",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    UploadedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_images", x => x.Id);
                    table.ForeignKey(
                        name: "FK_camp_images_camps_CampId",
                        column: x => x.CampId,
                        principalTable: "camps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "camp_seasons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampId = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NameLockDate = table.Column<LocalDate>(type: "date", nullable: true),
                    NameLockedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    BlurbLong = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    BlurbShort = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Languages = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AcceptingMembers = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    KidsWelcome = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    KidsVisiting = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    KidsAreaDescription = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    HasPerformanceSpace = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PerformanceTypes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Vibes = table.Column<string>(type: "jsonb", nullable: false),
                    AdultPlayspace = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MemberCount = table.Column<int>(type: "integer", nullable: false),
                    SpaceRequirement = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SoundZone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ElectricalGrid = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ResolvedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    EeSlotCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_seasons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_camp_seasons_camps_CampId",
                        column: x => x.CampId,
                        principalTable: "camps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                    RemovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    HasEarlyEntry = table.Column<bool>(type: "boolean", nullable: false)
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

            migrationBuilder.InsertData(
                table: "camp_settings",
                columns: new[] { "Id", "EeStartDate", "OpenSeasons", "PublicYear" },
                values: new object[] { new Guid("00000000-0000-0000-0010-000000000001"), null, "[2026]", 2026 });

            migrationBuilder.CreateIndex(
                name: "IX_camp_historical_names_CampId",
                table: "camp_historical_names",
                column: "CampId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_images_CampId",
                table: "camp_images",
                column: "CampId");

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
                name: "IX_camp_seasons_CampId_Year",
                table: "camp_seasons",
                columns: new[] { "CampId", "Year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_camp_seasons_Status",
                table: "camp_seasons",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_camps_Slug",
                table: "camps",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "camp_historical_names");

            migrationBuilder.DropTable(
                name: "camp_images");

            migrationBuilder.DropTable(
                name: "camp_role_assignments");

            migrationBuilder.DropTable(
                name: "camp_settings");

            migrationBuilder.DropTable(
                name: "camp_members");

            migrationBuilder.DropTable(
                name: "camp_role_definitions");

            migrationBuilder.DropTable(
                name: "camp_seasons");

            migrationBuilder.DropTable(
                name: "camps");
        }
    }
}
