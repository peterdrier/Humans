using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Rideshare.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialRideshare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rideshare_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Direction = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PickupPlaceLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PickupLatitude = table.Column<double>(type: "double precision", nullable: false),
                    PickupLongitude = table.Column<double>(type: "double precision", nullable: false),
                    DesiredDate = table.Column<LocalDate>(type: "date", nullable: false),
                    PartySize = table.Column<int>(type: "integer", nullable: false),
                    LuggageLoad = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CanContributeToFuel = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rideshare_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "rideshare_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    DestinationLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DestinationLatitude = table.Column<double>(type: "double precision", nullable: false),
                    DestinationLongitude = table.Column<double>(type: "double precision", nullable: false),
                    InboundWindowStart = table.Column<LocalDate>(type: "date", nullable: false),
                    InboundWindowEnd = table.Column<LocalDate>(type: "date", nullable: false),
                    OutboundWindowStart = table.Column<LocalDate>(type: "date", nullable: false),
                    OutboundWindowEnd = table.Column<LocalDate>(type: "date", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rideshare_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "rideshare_trips",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Direction = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MemberPlaceLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MemberLatitude = table.Column<double>(type: "double precision", nullable: false),
                    MemberLongitude = table.Column<double>(type: "double precision", nullable: false),
                    WaypointsJson = table.Column<string>(type: "text", nullable: true),
                    RouteGeoJson = table.Column<string>(type: "text", nullable: true),
                    DepartureDate = table.Column<LocalDate>(type: "date", nullable: false),
                    ExpectedDurationDays = table.Column<int>(type: "integer", nullable: false),
                    OvernightPlan = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    VehicleType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SeatsOffered = table.Column<int>(type: "integer", nullable: false),
                    LuggageCapacity = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CapacityNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Restrictions = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    WillingToDetour = table.Column<bool>(type: "boolean", nullable: false),
                    CostSharing = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CostNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LinkedTripId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rideshare_trips", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "rideshare_interests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FromUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    Seats = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    RespondedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rideshare_interests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rideshare_interests_rideshare_requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "rideshare_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_rideshare_interests_rideshare_trips_TripId",
                        column: x => x.TripId,
                        principalTable: "rideshare_trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rideshare_interests_FromUserId",
                table: "rideshare_interests",
                column: "FromUserId");

            migrationBuilder.CreateIndex(
                name: "IX_rideshare_interests_RequestId",
                table: "rideshare_interests",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_rideshare_interests_TripId",
                table: "rideshare_interests",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_rideshare_requests_UserId",
                table: "rideshare_requests",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_rideshare_requests_Year",
                table: "rideshare_requests",
                column: "Year");

            migrationBuilder.CreateIndex(
                name: "IX_rideshare_settings_Year",
                table: "rideshare_settings",
                column: "Year",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rideshare_trips_UserId",
                table: "rideshare_trips",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_rideshare_trips_Year",
                table: "rideshare_trips",
                column: "Year");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rideshare_interests");

            migrationBuilder.DropTable(
                name: "rideshare_settings");

            migrationBuilder.DropTable(
                name: "rideshare_requests");

            migrationBuilder.DropTable(
                name: "rideshare_trips");
        }
    }
}
