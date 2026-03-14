using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS barrio_historical_names");
            migrationBuilder.Sql("DROP TABLE IF EXISTS barrio_images");
            migrationBuilder.Sql("DROP TABLE IF EXISTS barrio_leads");
            migrationBuilder.Sql("DROP TABLE IF EXISTS barrio_seasons");
            migrationBuilder.Sql("DROP TABLE IF EXISTS barrio_settings");
            migrationBuilder.Sql("DROP TABLE IF EXISTS barrios");

            migrationBuilder.CreateTable(
                name: "camp_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicYear = table.Column<int>(type: "integer", nullable: false),
                    OpenSeasons = table.Column<string>(type: "jsonb", nullable: false)
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
                    ContactMethod = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    IsSwissCamp = table.Column<bool>(type: "boolean", nullable: false),
                    TimesAtNowhere = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_camps_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                name: "camp_leads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    JoinedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LeftAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_leads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_camp_leads_camps_CampId",
                        column: x => x.CampId,
                        principalTable: "camps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_camp_leads_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                    ContainerCount = table.Column<int>(type: "integer", nullable: false),
                    ContainerNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ElectricalGrid = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ResolvedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
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
                    table.ForeignKey(
                        name: "FK_camp_seasons_users_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.InsertData(
                table: "camp_settings",
                columns: new[] { "Id", "OpenSeasons", "PublicYear" },
                values: new object[] { new Guid("00000000-0000-0000-0010-000000000001"), "[2026]", 2026 });

            migrationBuilder.CreateIndex(
                name: "IX_camp_historical_names_CampId",
                table: "camp_historical_names",
                column: "CampId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_images_CampId",
                table: "camp_images",
                column: "CampId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_leads_active_unique",
                table: "camp_leads",
                columns: new[] { "CampId", "UserId" },
                unique: true,
                filter: "\"LeftAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_camp_leads_UserId",
                table: "camp_leads",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_seasons_CampId_Year",
                table: "camp_seasons",
                columns: new[] { "CampId", "Year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_camp_seasons_ReviewedByUserId",
                table: "camp_seasons",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_seasons_Status",
                table: "camp_seasons",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_camps_CreatedByUserId",
                table: "camps",
                column: "CreatedByUserId");

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
                name: "camp_leads");

            migrationBuilder.DropTable(
                name: "camp_seasons");

            migrationBuilder.DropTable(
                name: "camp_settings");

            migrationBuilder.DropTable(
                name: "camps");

            migrationBuilder.CreateTable(
                name: "barrio_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OpenSeasons = table.Column<string>(type: "jsonb", nullable: false),
                    PublicYear = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_barrio_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "barrios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ContactMethod = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ContactPhone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    IsSwissCamp = table.Column<bool>(type: "boolean", nullable: false),
                    Slug = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TimesAtNowhere = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    WebOrSocialUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_barrios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_barrios_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "barrio_historical_names",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BarrioId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_barrio_historical_names", x => x.Id);
                    table.ForeignKey(
                        name: "FK_barrio_historical_names_barrios_BarrioId",
                        column: x => x.BarrioId,
                        principalTable: "barrios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "barrio_images",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BarrioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    UploadedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_barrio_images", x => x.Id);
                    table.ForeignKey(
                        name: "FK_barrio_images_barrios_BarrioId",
                        column: x => x.BarrioId,
                        principalTable: "barrios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "barrio_leads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BarrioId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JoinedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LeftAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    Role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_barrio_leads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_barrio_leads_barrios_BarrioId",
                        column: x => x.BarrioId,
                        principalTable: "barrios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_barrio_leads_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "barrio_seasons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BarrioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AcceptingMembers = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    AdultPlayspace = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    BlurbLong = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    BlurbShort = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ContainerCount = table.Column<int>(type: "integer", nullable: false),
                    ContainerNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ElectricalGrid = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    HasPerformanceSpace = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    KidsAreaDescription = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    KidsVisiting = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    KidsWelcome = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Languages = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MemberCount = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NameLockDate = table.Column<LocalDate>(type: "date", nullable: true),
                    NameLockedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    PerformanceTypes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ResolvedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ReviewNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SoundZone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SpaceRequirement = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Vibes = table.Column<string>(type: "jsonb", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_barrio_seasons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_barrio_seasons_barrios_BarrioId",
                        column: x => x.BarrioId,
                        principalTable: "barrios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_barrio_seasons_users_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.InsertData(
                table: "barrio_settings",
                columns: new[] { "Id", "OpenSeasons", "PublicYear" },
                values: new object[] { new Guid("00000000-0000-0000-0010-000000000001"), "[2026]", 2026 });

            migrationBuilder.CreateIndex(
                name: "IX_barrio_historical_names_BarrioId",
                table: "barrio_historical_names",
                column: "BarrioId");

            migrationBuilder.CreateIndex(
                name: "IX_barrio_images_BarrioId",
                table: "barrio_images",
                column: "BarrioId");

            migrationBuilder.CreateIndex(
                name: "IX_barrio_leads_active_unique",
                table: "barrio_leads",
                columns: new[] { "BarrioId", "UserId" },
                unique: true,
                filter: "\"LeftAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_barrio_leads_UserId",
                table: "barrio_leads",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_barrio_seasons_BarrioId_Year",
                table: "barrio_seasons",
                columns: new[] { "BarrioId", "Year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_barrio_seasons_ReviewedByUserId",
                table: "barrio_seasons",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_barrio_seasons_Status",
                table: "barrio_seasons",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_barrios_CreatedByUserId",
                table: "barrios",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_barrios_Slug",
                table: "barrios",
                column: "Slug",
                unique: true);
        }
    }
}
