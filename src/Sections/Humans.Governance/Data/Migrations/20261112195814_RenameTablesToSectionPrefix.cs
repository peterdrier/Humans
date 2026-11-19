using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Governance.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameTablesToSectionPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_application_state_history_applications_ApplicationId",
                table: "application_state_history");

            migrationBuilder.DropForeignKey(
                name: "FK_assembly_ballot_history_assembly_ballots_BallotId",
                table: "assembly_ballot_history");

            migrationBuilder.DropForeignKey(
                name: "FK_assembly_ballots_assembly_vote_roster_RosterId",
                table: "assembly_ballots");

            migrationBuilder.DropForeignKey(
                name: "FK_assembly_ballots_assembly_votes_VoteId",
                table: "assembly_ballots");

            migrationBuilder.DropForeignKey(
                name: "FK_assembly_vote_options_assembly_votes_VoteId",
                table: "assembly_vote_options");

            migrationBuilder.DropForeignKey(
                name: "FK_assembly_vote_peeks_assembly_votes_VoteId",
                table: "assembly_vote_peeks");

            migrationBuilder.DropForeignKey(
                name: "FK_assembly_vote_roster_assembly_votes_VoteId",
                table: "assembly_vote_roster");

            migrationBuilder.DropForeignKey(
                name: "FK_board_votes_applications_ApplicationId",
                table: "board_votes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_board_votes",
                table: "board_votes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_assembly_votes",
                table: "assembly_votes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_assembly_vote_roster",
                table: "assembly_vote_roster");

            migrationBuilder.DropPrimaryKey(
                name: "PK_assembly_vote_peeks",
                table: "assembly_vote_peeks");

            migrationBuilder.DropPrimaryKey(
                name: "PK_assembly_vote_options",
                table: "assembly_vote_options");

            migrationBuilder.DropPrimaryKey(
                name: "PK_assembly_ballots",
                table: "assembly_ballots");

            migrationBuilder.DropPrimaryKey(
                name: "PK_assembly_ballot_history",
                table: "assembly_ballot_history");

            migrationBuilder.DropPrimaryKey(
                name: "PK_applications",
                table: "applications");

            migrationBuilder.DropPrimaryKey(
                name: "PK_application_state_history",
                table: "application_state_history");

            migrationBuilder.RenameTable(
                name: "board_votes",
                newName: "governance_board_votes");

            migrationBuilder.RenameTable(
                name: "assembly_votes",
                newName: "governance_assembly_votes");

            migrationBuilder.RenameTable(
                name: "assembly_vote_roster",
                newName: "governance_assembly_vote_roster_entries");

            migrationBuilder.RenameTable(
                name: "assembly_vote_peeks",
                newName: "governance_assembly_vote_peeks");

            migrationBuilder.RenameTable(
                name: "assembly_vote_options",
                newName: "governance_assembly_vote_options");

            migrationBuilder.RenameTable(
                name: "assembly_ballots",
                newName: "governance_assembly_ballots");

            migrationBuilder.RenameTable(
                name: "assembly_ballot_history",
                newName: "governance_assembly_ballot_history");

            migrationBuilder.RenameTable(
                name: "applications",
                newName: "governance_applications");

            migrationBuilder.RenameTable(
                name: "application_state_history",
                newName: "governance_application_state_history");

            migrationBuilder.RenameIndex(
                name: "IX_board_votes_ApplicationId_BoardMemberUserId",
                table: "governance_board_votes",
                newName: "IX_governance_board_votes_ApplicationId_BoardMemberUserId");

            migrationBuilder.RenameIndex(
                name: "IX_board_votes_ApplicationId",
                table: "governance_board_votes",
                newName: "IX_governance_board_votes_ApplicationId");

            migrationBuilder.RenameIndex(
                name: "IX_assembly_votes_Status_ClosesAt",
                table: "governance_assembly_votes",
                newName: "IX_governance_assembly_votes_Status_ClosesAt");

            migrationBuilder.RenameIndex(
                name: "IX_assembly_votes_Status",
                table: "governance_assembly_votes",
                newName: "IX_governance_assembly_votes_Status");

            migrationBuilder.RenameIndex(
                name: "IX_assembly_vote_roster_VoteId_UserId",
                table: "governance_assembly_vote_roster_entries",
                newName: "IX_governance_assembly_vote_roster_entries_VoteId_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_assembly_vote_roster_VoteId",
                table: "governance_assembly_vote_roster_entries",
                newName: "IX_governance_assembly_vote_roster_entries_VoteId");

            migrationBuilder.RenameIndex(
                name: "IX_assembly_vote_peeks_VoteId",
                table: "governance_assembly_vote_peeks",
                newName: "IX_governance_assembly_vote_peeks_VoteId");

            migrationBuilder.RenameIndex(
                name: "IX_assembly_vote_options_VoteId",
                table: "governance_assembly_vote_options",
                newName: "IX_governance_assembly_vote_options_VoteId");

            migrationBuilder.RenameIndex(
                name: "IX_assembly_ballots_VoteId_RosterId",
                table: "governance_assembly_ballots",
                newName: "IX_governance_assembly_ballots_VoteId_RosterId");

            migrationBuilder.RenameIndex(
                name: "IX_assembly_ballots_RosterId",
                table: "governance_assembly_ballots",
                newName: "IX_governance_assembly_ballots_RosterId");

            migrationBuilder.RenameIndex(
                name: "IX_assembly_ballot_history_BallotId_Revision",
                table: "governance_assembly_ballot_history",
                newName: "IX_governance_assembly_ballot_history_BallotId_Revision");

            migrationBuilder.RenameIndex(
                name: "IX_applications_UserId_Status",
                table: "governance_applications",
                newName: "IX_governance_applications_UserId_Status");

            migrationBuilder.RenameIndex(
                name: "IX_applications_UserId",
                table: "governance_applications",
                newName: "IX_governance_applications_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_applications_SubmittedAt",
                table: "governance_applications",
                newName: "IX_governance_applications_SubmittedAt");

            migrationBuilder.RenameIndex(
                name: "IX_applications_Status",
                table: "governance_applications",
                newName: "IX_governance_applications_Status");

            migrationBuilder.RenameIndex(
                name: "IX_applications_MembershipTier",
                table: "governance_applications",
                newName: "IX_governance_applications_MembershipTier");

            migrationBuilder.RenameIndex(
                name: "IX_application_state_history_ChangedAt",
                table: "governance_application_state_history",
                newName: "IX_governance_application_state_history_ChangedAt");

            migrationBuilder.RenameIndex(
                name: "IX_application_state_history_ApplicationId",
                table: "governance_application_state_history",
                newName: "IX_governance_application_state_history_ApplicationId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_governance_board_votes",
                table: "governance_board_votes",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_governance_assembly_votes",
                table: "governance_assembly_votes",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_governance_assembly_vote_roster_entries",
                table: "governance_assembly_vote_roster_entries",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_governance_assembly_vote_peeks",
                table: "governance_assembly_vote_peeks",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_governance_assembly_vote_options",
                table: "governance_assembly_vote_options",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_governance_assembly_ballots",
                table: "governance_assembly_ballots",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_governance_assembly_ballot_history",
                table: "governance_assembly_ballot_history",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_governance_applications",
                table: "governance_applications",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_governance_application_state_history",
                table: "governance_application_state_history",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_governance_application_state_history_governance_application~",
                table: "governance_application_state_history",
                column: "ApplicationId",
                principalTable: "governance_applications",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_governance_assembly_ballot_history_governance_assembly_ball~",
                table: "governance_assembly_ballot_history",
                column: "BallotId",
                principalTable: "governance_assembly_ballots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_governance_assembly_ballots_governance_assembly_vote_roster~",
                table: "governance_assembly_ballots",
                column: "RosterId",
                principalTable: "governance_assembly_vote_roster_entries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_governance_assembly_ballots_governance_assembly_votes_VoteId",
                table: "governance_assembly_ballots",
                column: "VoteId",
                principalTable: "governance_assembly_votes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_governance_assembly_vote_options_governance_assembly_votes_~",
                table: "governance_assembly_vote_options",
                column: "VoteId",
                principalTable: "governance_assembly_votes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_governance_assembly_vote_peeks_governance_assembly_votes_Vo~",
                table: "governance_assembly_vote_peeks",
                column: "VoteId",
                principalTable: "governance_assembly_votes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_governance_assembly_vote_roster_entries_governance_assembly~",
                table: "governance_assembly_vote_roster_entries",
                column: "VoteId",
                principalTable: "governance_assembly_votes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_governance_board_votes_governance_applications_ApplicationId",
                table: "governance_board_votes",
                column: "ApplicationId",
                principalTable: "governance_applications",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_governance_application_state_history_governance_application~",
                table: "governance_application_state_history");

            migrationBuilder.DropForeignKey(
                name: "FK_governance_assembly_ballot_history_governance_assembly_ball~",
                table: "governance_assembly_ballot_history");

            migrationBuilder.DropForeignKey(
                name: "FK_governance_assembly_ballots_governance_assembly_vote_roster~",
                table: "governance_assembly_ballots");

            migrationBuilder.DropForeignKey(
                name: "FK_governance_assembly_ballots_governance_assembly_votes_VoteId",
                table: "governance_assembly_ballots");

            migrationBuilder.DropForeignKey(
                name: "FK_governance_assembly_vote_options_governance_assembly_votes_~",
                table: "governance_assembly_vote_options");

            migrationBuilder.DropForeignKey(
                name: "FK_governance_assembly_vote_peeks_governance_assembly_votes_Vo~",
                table: "governance_assembly_vote_peeks");

            migrationBuilder.DropForeignKey(
                name: "FK_governance_assembly_vote_roster_entries_governance_assembly~",
                table: "governance_assembly_vote_roster_entries");

            migrationBuilder.DropForeignKey(
                name: "FK_governance_board_votes_governance_applications_ApplicationId",
                table: "governance_board_votes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_governance_board_votes",
                table: "governance_board_votes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_governance_assembly_votes",
                table: "governance_assembly_votes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_governance_assembly_vote_roster_entries",
                table: "governance_assembly_vote_roster_entries");

            migrationBuilder.DropPrimaryKey(
                name: "PK_governance_assembly_vote_peeks",
                table: "governance_assembly_vote_peeks");

            migrationBuilder.DropPrimaryKey(
                name: "PK_governance_assembly_vote_options",
                table: "governance_assembly_vote_options");

            migrationBuilder.DropPrimaryKey(
                name: "PK_governance_assembly_ballots",
                table: "governance_assembly_ballots");

            migrationBuilder.DropPrimaryKey(
                name: "PK_governance_assembly_ballot_history",
                table: "governance_assembly_ballot_history");

            migrationBuilder.DropPrimaryKey(
                name: "PK_governance_applications",
                table: "governance_applications");

            migrationBuilder.DropPrimaryKey(
                name: "PK_governance_application_state_history",
                table: "governance_application_state_history");

            migrationBuilder.RenameTable(
                name: "governance_board_votes",
                newName: "board_votes");

            migrationBuilder.RenameTable(
                name: "governance_assembly_votes",
                newName: "assembly_votes");

            migrationBuilder.RenameTable(
                name: "governance_assembly_vote_roster_entries",
                newName: "assembly_vote_roster");

            migrationBuilder.RenameTable(
                name: "governance_assembly_vote_peeks",
                newName: "assembly_vote_peeks");

            migrationBuilder.RenameTable(
                name: "governance_assembly_vote_options",
                newName: "assembly_vote_options");

            migrationBuilder.RenameTable(
                name: "governance_assembly_ballots",
                newName: "assembly_ballots");

            migrationBuilder.RenameTable(
                name: "governance_assembly_ballot_history",
                newName: "assembly_ballot_history");

            migrationBuilder.RenameTable(
                name: "governance_applications",
                newName: "applications");

            migrationBuilder.RenameTable(
                name: "governance_application_state_history",
                newName: "application_state_history");

            migrationBuilder.RenameIndex(
                name: "IX_governance_board_votes_ApplicationId_BoardMemberUserId",
                table: "board_votes",
                newName: "IX_board_votes_ApplicationId_BoardMemberUserId");

            migrationBuilder.RenameIndex(
                name: "IX_governance_board_votes_ApplicationId",
                table: "board_votes",
                newName: "IX_board_votes_ApplicationId");

            migrationBuilder.RenameIndex(
                name: "IX_governance_assembly_votes_Status_ClosesAt",
                table: "assembly_votes",
                newName: "IX_assembly_votes_Status_ClosesAt");

            migrationBuilder.RenameIndex(
                name: "IX_governance_assembly_votes_Status",
                table: "assembly_votes",
                newName: "IX_assembly_votes_Status");

            migrationBuilder.RenameIndex(
                name: "IX_governance_assembly_vote_roster_entries_VoteId_UserId",
                table: "assembly_vote_roster",
                newName: "IX_assembly_vote_roster_VoteId_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_governance_assembly_vote_roster_entries_VoteId",
                table: "assembly_vote_roster",
                newName: "IX_assembly_vote_roster_VoteId");

            migrationBuilder.RenameIndex(
                name: "IX_governance_assembly_vote_peeks_VoteId",
                table: "assembly_vote_peeks",
                newName: "IX_assembly_vote_peeks_VoteId");

            migrationBuilder.RenameIndex(
                name: "IX_governance_assembly_vote_options_VoteId",
                table: "assembly_vote_options",
                newName: "IX_assembly_vote_options_VoteId");

            migrationBuilder.RenameIndex(
                name: "IX_governance_assembly_ballots_VoteId_RosterId",
                table: "assembly_ballots",
                newName: "IX_assembly_ballots_VoteId_RosterId");

            migrationBuilder.RenameIndex(
                name: "IX_governance_assembly_ballots_RosterId",
                table: "assembly_ballots",
                newName: "IX_assembly_ballots_RosterId");

            migrationBuilder.RenameIndex(
                name: "IX_governance_assembly_ballot_history_BallotId_Revision",
                table: "assembly_ballot_history",
                newName: "IX_assembly_ballot_history_BallotId_Revision");

            migrationBuilder.RenameIndex(
                name: "IX_governance_applications_UserId_Status",
                table: "applications",
                newName: "IX_applications_UserId_Status");

            migrationBuilder.RenameIndex(
                name: "IX_governance_applications_UserId",
                table: "applications",
                newName: "IX_applications_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_governance_applications_SubmittedAt",
                table: "applications",
                newName: "IX_applications_SubmittedAt");

            migrationBuilder.RenameIndex(
                name: "IX_governance_applications_Status",
                table: "applications",
                newName: "IX_applications_Status");

            migrationBuilder.RenameIndex(
                name: "IX_governance_applications_MembershipTier",
                table: "applications",
                newName: "IX_applications_MembershipTier");

            migrationBuilder.RenameIndex(
                name: "IX_governance_application_state_history_ChangedAt",
                table: "application_state_history",
                newName: "IX_application_state_history_ChangedAt");

            migrationBuilder.RenameIndex(
                name: "IX_governance_application_state_history_ApplicationId",
                table: "application_state_history",
                newName: "IX_application_state_history_ApplicationId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_board_votes",
                table: "board_votes",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_assembly_votes",
                table: "assembly_votes",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_assembly_vote_roster",
                table: "assembly_vote_roster",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_assembly_vote_peeks",
                table: "assembly_vote_peeks",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_assembly_vote_options",
                table: "assembly_vote_options",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_assembly_ballots",
                table: "assembly_ballots",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_assembly_ballot_history",
                table: "assembly_ballot_history",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_applications",
                table: "applications",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_application_state_history",
                table: "application_state_history",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_application_state_history_applications_ApplicationId",
                table: "application_state_history",
                column: "ApplicationId",
                principalTable: "applications",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_assembly_ballot_history_assembly_ballots_BallotId",
                table: "assembly_ballot_history",
                column: "BallotId",
                principalTable: "assembly_ballots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_assembly_ballots_assembly_vote_roster_RosterId",
                table: "assembly_ballots",
                column: "RosterId",
                principalTable: "assembly_vote_roster",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_assembly_ballots_assembly_votes_VoteId",
                table: "assembly_ballots",
                column: "VoteId",
                principalTable: "assembly_votes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_assembly_vote_options_assembly_votes_VoteId",
                table: "assembly_vote_options",
                column: "VoteId",
                principalTable: "assembly_votes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_assembly_vote_peeks_assembly_votes_VoteId",
                table: "assembly_vote_peeks",
                column: "VoteId",
                principalTable: "assembly_votes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_assembly_vote_roster_assembly_votes_VoteId",
                table: "assembly_vote_roster",
                column: "VoteId",
                principalTable: "assembly_votes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_board_votes_applications_ApplicationId",
                table: "board_votes",
                column: "ApplicationId",
                principalTable: "applications",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
