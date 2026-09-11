using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Governance.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAssemblyVotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assembly_votes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    OfficialText = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    OfficialCulture = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    InfoUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RequiredMajority = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IndicativeAudience = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    BallotDisclosure = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AssemblyDate = table.Column<LocalDate>(type: "date", nullable: true),
                    ClosesAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    OpenedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    OpenedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClosedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ClosedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assembly_votes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "assembly_vote_options",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Label = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assembly_vote_options", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assembly_vote_options_assembly_votes_VoteId",
                        column: x => x.VoteId,
                        principalTable: "assembly_votes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assembly_vote_peeks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdminUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeekedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assembly_vote_peeks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assembly_vote_peeks_assembly_votes_VoteId",
                        column: x => x.VoteId,
                        principalTable: "assembly_votes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assembly_vote_roster",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Tier = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IsBoardMember = table.Column<bool>(type: "boolean", nullable: false),
                    IsOfficial = table.Column<bool>(type: "boolean", nullable: false),
                    NotifiedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ReminderSentAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assembly_vote_roster", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assembly_vote_roster_assembly_votes_VoteId",
                        column: x => x.VoteId,
                        principalTable: "assembly_votes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assembly_ballots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    RosterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Choice = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Ranking = table.Column<string>(type: "jsonb", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    CastAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assembly_ballots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assembly_ballots_assembly_vote_roster_RosterId",
                        column: x => x.RosterId,
                        principalTable: "assembly_vote_roster",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_assembly_ballots_assembly_votes_VoteId",
                        column: x => x.VoteId,
                        principalTable: "assembly_votes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assembly_ballot_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BallotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Choice = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Ranking = table.Column<string>(type: "jsonb", nullable: true),
                    RecordedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assembly_ballot_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assembly_ballot_history_assembly_ballots_BallotId",
                        column: x => x.BallotId,
                        principalTable: "assembly_ballots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assembly_ballot_history_BallotId_Revision",
                table: "assembly_ballot_history",
                columns: new[] { "BallotId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assembly_ballots_RosterId",
                table: "assembly_ballots",
                column: "RosterId");

            migrationBuilder.CreateIndex(
                name: "IX_assembly_ballots_VoteId_RosterId",
                table: "assembly_ballots",
                columns: new[] { "VoteId", "RosterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assembly_vote_options_VoteId",
                table: "assembly_vote_options",
                column: "VoteId");

            migrationBuilder.CreateIndex(
                name: "IX_assembly_vote_peeks_VoteId",
                table: "assembly_vote_peeks",
                column: "VoteId");

            migrationBuilder.CreateIndex(
                name: "IX_assembly_vote_roster_VoteId",
                table: "assembly_vote_roster",
                column: "VoteId");

            migrationBuilder.CreateIndex(
                name: "IX_assembly_vote_roster_VoteId_UserId",
                table: "assembly_vote_roster",
                columns: new[] { "VoteId", "UserId" },
                unique: true,
                filter: "\"UserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_assembly_votes_Status",
                table: "assembly_votes",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_assembly_votes_Status_ClosesAt",
                table: "assembly_votes",
                columns: new[] { "Status", "ClosesAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assembly_ballot_history");

            migrationBuilder.DropTable(
                name: "assembly_vote_options");

            migrationBuilder.DropTable(
                name: "assembly_vote_peeks");

            migrationBuilder.DropTable(
                name: "assembly_ballots");

            migrationBuilder.DropTable(
                name: "assembly_vote_roster");

            migrationBuilder.DropTable(
                name: "assembly_votes");
        }
    }
}
