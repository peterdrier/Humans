using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropCrossSectionForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_application_state_history_users_ChangedByUserId",
                table: "application_state_history");

            migrationBuilder.DropForeignKey(
                name: "FK_applications_users_ReviewedByUserId",
                table: "applications");

            migrationBuilder.DropForeignKey(
                name: "FK_applications_users_UserId",
                table: "applications");

            migrationBuilder.DropForeignKey(
                name: "FK_audit_log_google_resources_ResourceId",
                table: "audit_log");

            migrationBuilder.DropForeignKey(
                name: "FK_audit_log_users_ActorUserId",
                table: "audit_log");

            migrationBuilder.DropForeignKey(
                name: "FK_board_votes_users_BoardMemberUserId",
                table: "board_votes");

            migrationBuilder.DropForeignKey(
                name: "FK_budget_audit_logs_users_ActorUserId",
                table: "budget_audit_logs");

            migrationBuilder.DropForeignKey(
                name: "FK_budget_categories_teams_TeamId",
                table: "budget_categories");

            migrationBuilder.DropForeignKey(
                name: "FK_budget_line_items_teams_ResponsibleTeamId",
                table: "budget_line_items");

            migrationBuilder.DropForeignKey(
                name: "FK_calendar_events_teams_OwningTeamId",
                table: "calendar_events");

            migrationBuilder.DropForeignKey(
                name: "FK_camp_polygon_histories_camp_seasons_CampSeasonId",
                table: "camp_polygon_histories");

            migrationBuilder.DropForeignKey(
                name: "FK_camp_polygon_histories_users_ModifiedByUserId",
                table: "camp_polygon_histories");

            migrationBuilder.DropForeignKey(
                name: "FK_camp_polygons_camp_seasons_CampSeasonId",
                table: "camp_polygons");

            migrationBuilder.DropForeignKey(
                name: "FK_camp_polygons_users_LastModifiedByUserId",
                table: "camp_polygons");

            migrationBuilder.DropForeignKey(
                name: "FK_camp_seasons_users_ReviewedByUserId",
                table: "camp_seasons");

            migrationBuilder.DropForeignKey(
                name: "FK_campaign_grants_users_UserId",
                table: "campaign_grants");

            migrationBuilder.DropForeignKey(
                name: "FK_campaigns_users_CreatedByUserId",
                table: "campaigns");

            migrationBuilder.DropForeignKey(
                name: "FK_camps_users_CreatedByUserId",
                table: "camps");

            migrationBuilder.DropForeignKey(
                name: "FK_consent_records_users_UserId",
                table: "consent_records");

            migrationBuilder.DropForeignKey(
                name: "FK_email_outbox_messages_campaign_grants_CampaignGrantId",
                table: "email_outbox_messages");

            migrationBuilder.DropForeignKey(
                name: "FK_email_outbox_messages_shift_signups_ShiftSignupId",
                table: "email_outbox_messages");

            migrationBuilder.DropForeignKey(
                name: "FK_email_outbox_messages_users_UserId",
                table: "email_outbox_messages");

            migrationBuilder.DropForeignKey(
                name: "FK_feedback_messages_users_SenderUserId",
                table: "feedback_messages");

            migrationBuilder.DropForeignKey(
                name: "FK_feedback_reports_teams_AssignedToTeamId",
                table: "feedback_reports");

            migrationBuilder.DropForeignKey(
                name: "FK_feedback_reports_users_AssignedToUserId",
                table: "feedback_reports");

            migrationBuilder.DropForeignKey(
                name: "FK_feedback_reports_users_ResolvedByUserId",
                table: "feedback_reports");

            migrationBuilder.DropForeignKey(
                name: "FK_feedback_reports_users_UserId",
                table: "feedback_reports");

            migrationBuilder.DropForeignKey(
                name: "FK_general_availability_users_UserId",
                table: "general_availability");

            migrationBuilder.DropForeignKey(
                name: "FK_google_resources_teams_TeamId",
                table: "google_resources");

            migrationBuilder.DropForeignKey(
                name: "FK_google_sync_outbox_teams_TeamId",
                table: "google_sync_outbox");

            migrationBuilder.DropForeignKey(
                name: "FK_google_sync_outbox_users_UserId",
                table: "google_sync_outbox");

            migrationBuilder.DropForeignKey(
                name: "FK_issue_comments_users_SenderUserId",
                table: "issue_comments");

            migrationBuilder.DropForeignKey(
                name: "FK_issues_users_AssigneeUserId",
                table: "issues");

            migrationBuilder.DropForeignKey(
                name: "FK_issues_users_ReporterUserId",
                table: "issues");

            migrationBuilder.DropForeignKey(
                name: "FK_issues_users_ResolvedByUserId",
                table: "issues");

            migrationBuilder.DropForeignKey(
                name: "FK_legal_documents_teams_TeamId",
                table: "legal_documents");

            migrationBuilder.DropForeignKey(
                name: "FK_notification_recipients_users_UserId",
                table: "notification_recipients");

            migrationBuilder.DropForeignKey(
                name: "FK_notifications_users_ResolvedByUserId",
                table: "notifications");

            migrationBuilder.DropForeignKey(
                name: "FK_role_assignments_users_CreatedByUserId",
                table: "role_assignments");

            migrationBuilder.DropForeignKey(
                name: "FK_role_assignments_users_UserId",
                table: "role_assignments");

            migrationBuilder.DropForeignKey(
                name: "FK_rotas_teams_TeamId",
                table: "rotas");

            migrationBuilder.DropForeignKey(
                name: "FK_shift_signups_users_EnrolledByUserId",
                table: "shift_signups");

            migrationBuilder.DropForeignKey(
                name: "FK_shift_signups_users_ReviewedByUserId",
                table: "shift_signups");

            migrationBuilder.DropForeignKey(
                name: "FK_shift_signups_users_UserId",
                table: "shift_signups");

            migrationBuilder.DropForeignKey(
                name: "FK_sync_service_settings_users_UpdatedByUserId",
                table: "sync_service_settings");

            migrationBuilder.DropForeignKey(
                name: "FK_team_join_request_state_history_users_ChangedByUserId",
                table: "team_join_request_state_history");

            migrationBuilder.DropForeignKey(
                name: "FK_team_join_requests_users_ReviewedByUserId",
                table: "team_join_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_team_join_requests_users_UserId",
                table: "team_join_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_team_members_users_UserId",
                table: "team_members");

            migrationBuilder.DropForeignKey(
                name: "FK_team_role_assignments_users_AssignedByUserId",
                table: "team_role_assignments");

            migrationBuilder.DropForeignKey(
                name: "FK_ticket_attendees_users_MatchedUserId",
                table: "ticket_attendees");

            migrationBuilder.DropForeignKey(
                name: "FK_ticket_orders_users_MatchedUserId",
                table: "ticket_orders");

            migrationBuilder.DropForeignKey(
                name: "FK_volunteer_event_profiles_users_UserId",
                table: "volunteer_event_profiles");

            migrationBuilder.DropForeignKey(
                name: "FK_volunteer_tag_preferences_users_UserId",
                table: "volunteer_tag_preferences");

            migrationBuilder.DropIndex(
                name: "IX_team_role_assignments_AssignedByUserId",
                table: "team_role_assignments");

            migrationBuilder.DropIndex(
                name: "IX_team_join_requests_ReviewedByUserId",
                table: "team_join_requests");

            migrationBuilder.DropIndex(
                name: "IX_team_join_request_state_history_ChangedByUserId",
                table: "team_join_request_state_history");

            migrationBuilder.DropIndex(
                name: "IX_sync_service_settings_UpdatedByUserId",
                table: "sync_service_settings");

            migrationBuilder.DropIndex(
                name: "IX_shift_signups_EnrolledByUserId",
                table: "shift_signups");

            migrationBuilder.DropIndex(
                name: "IX_shift_signups_ReviewedByUserId",
                table: "shift_signups");

            migrationBuilder.DropIndex(
                name: "IX_rotas_TeamId",
                table: "rotas");

            migrationBuilder.DropIndex(
                name: "IX_role_assignments_CreatedByUserId",
                table: "role_assignments");

            migrationBuilder.DropIndex(
                name: "IX_notifications_ResolvedByUserId",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "IX_issues_ResolvedByUserId",
                table: "issues");

            migrationBuilder.DropIndex(
                name: "IX_issue_comments_SenderUserId",
                table: "issue_comments");

            migrationBuilder.DropIndex(
                name: "IX_google_sync_outbox_UserId",
                table: "google_sync_outbox");

            migrationBuilder.DropIndex(
                name: "IX_feedback_reports_ResolvedByUserId",
                table: "feedback_reports");

            migrationBuilder.DropIndex(
                name: "IX_feedback_messages_SenderUserId",
                table: "feedback_messages");

            migrationBuilder.DropIndex(
                name: "IX_camps_CreatedByUserId",
                table: "camps");

            migrationBuilder.DropIndex(
                name: "IX_campaigns_CreatedByUserId",
                table: "campaigns");

            migrationBuilder.DropIndex(
                name: "IX_camp_seasons_ReviewedByUserId",
                table: "camp_seasons");

            migrationBuilder.DropIndex(
                name: "IX_camp_polygons_LastModifiedByUserId",
                table: "camp_polygons");

            migrationBuilder.DropIndex(
                name: "IX_camp_polygon_histories_ModifiedByUserId",
                table: "camp_polygon_histories");

            migrationBuilder.DropIndex(
                name: "IX_board_votes_BoardMemberUserId",
                table: "board_votes");

            migrationBuilder.DropIndex(
                name: "IX_audit_log_ActorUserId",
                table: "audit_log");

            migrationBuilder.DropIndex(
                name: "IX_applications_ReviewedByUserId",
                table: "applications");

            migrationBuilder.DropIndex(
                name: "IX_application_state_history_ChangedByUserId",
                table: "application_state_history");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_team_role_assignments_AssignedByUserId",
                table: "team_role_assignments",
                column: "AssignedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_team_join_requests_ReviewedByUserId",
                table: "team_join_requests",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_team_join_request_state_history_ChangedByUserId",
                table: "team_join_request_state_history",
                column: "ChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_sync_service_settings_UpdatedByUserId",
                table: "sync_service_settings",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_shift_signups_EnrolledByUserId",
                table: "shift_signups",
                column: "EnrolledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_shift_signups_ReviewedByUserId",
                table: "shift_signups",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_rotas_TeamId",
                table: "rotas",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_role_assignments_CreatedByUserId",
                table: "role_assignments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_ResolvedByUserId",
                table: "notifications",
                column: "ResolvedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_issues_ResolvedByUserId",
                table: "issues",
                column: "ResolvedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_issue_comments_SenderUserId",
                table: "issue_comments",
                column: "SenderUserId");

            migrationBuilder.CreateIndex(
                name: "IX_google_sync_outbox_UserId",
                table: "google_sync_outbox",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_ResolvedByUserId",
                table: "feedback_reports",
                column: "ResolvedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_messages_SenderUserId",
                table: "feedback_messages",
                column: "SenderUserId");

            migrationBuilder.CreateIndex(
                name: "IX_camps_CreatedByUserId",
                table: "camps",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_CreatedByUserId",
                table: "campaigns",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_seasons_ReviewedByUserId",
                table: "camp_seasons",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_polygons_LastModifiedByUserId",
                table: "camp_polygons",
                column: "LastModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_camp_polygon_histories_ModifiedByUserId",
                table: "camp_polygon_histories",
                column: "ModifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_board_votes_BoardMemberUserId",
                table: "board_votes",
                column: "BoardMemberUserId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_ActorUserId",
                table: "audit_log",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_applications_ReviewedByUserId",
                table: "applications",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_application_state_history_ChangedByUserId",
                table: "application_state_history",
                column: "ChangedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_application_state_history_users_ChangedByUserId",
                table: "application_state_history",
                column: "ChangedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_applications_users_ReviewedByUserId",
                table: "applications",
                column: "ReviewedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_applications_users_UserId",
                table: "applications",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_audit_log_google_resources_ResourceId",
                table: "audit_log",
                column: "ResourceId",
                principalTable: "google_resources",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_audit_log_users_ActorUserId",
                table: "audit_log",
                column: "ActorUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_board_votes_users_BoardMemberUserId",
                table: "board_votes",
                column: "BoardMemberUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_budget_audit_logs_users_ActorUserId",
                table: "budget_audit_logs",
                column: "ActorUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_budget_categories_teams_TeamId",
                table: "budget_categories",
                column: "TeamId",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_budget_line_items_teams_ResponsibleTeamId",
                table: "budget_line_items",
                column: "ResponsibleTeamId",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_calendar_events_teams_OwningTeamId",
                table: "calendar_events",
                column: "OwningTeamId",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_camp_polygon_histories_camp_seasons_CampSeasonId",
                table: "camp_polygon_histories",
                column: "CampSeasonId",
                principalTable: "camp_seasons",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_camp_polygon_histories_users_ModifiedByUserId",
                table: "camp_polygon_histories",
                column: "ModifiedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_camp_polygons_camp_seasons_CampSeasonId",
                table: "camp_polygons",
                column: "CampSeasonId",
                principalTable: "camp_seasons",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_camp_polygons_users_LastModifiedByUserId",
                table: "camp_polygons",
                column: "LastModifiedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_camp_seasons_users_ReviewedByUserId",
                table: "camp_seasons",
                column: "ReviewedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_campaign_grants_users_UserId",
                table: "campaign_grants",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_campaigns_users_CreatedByUserId",
                table: "campaigns",
                column: "CreatedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_camps_users_CreatedByUserId",
                table: "camps",
                column: "CreatedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_consent_records_users_UserId",
                table: "consent_records",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_email_outbox_messages_campaign_grants_CampaignGrantId",
                table: "email_outbox_messages",
                column: "CampaignGrantId",
                principalTable: "campaign_grants",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_email_outbox_messages_shift_signups_ShiftSignupId",
                table: "email_outbox_messages",
                column: "ShiftSignupId",
                principalTable: "shift_signups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_email_outbox_messages_users_UserId",
                table: "email_outbox_messages",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_feedback_messages_users_SenderUserId",
                table: "feedback_messages",
                column: "SenderUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_feedback_reports_teams_AssignedToTeamId",
                table: "feedback_reports",
                column: "AssignedToTeamId",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_feedback_reports_users_AssignedToUserId",
                table: "feedback_reports",
                column: "AssignedToUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_feedback_reports_users_ResolvedByUserId",
                table: "feedback_reports",
                column: "ResolvedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_feedback_reports_users_UserId",
                table: "feedback_reports",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_general_availability_users_UserId",
                table: "general_availability",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_google_resources_teams_TeamId",
                table: "google_resources",
                column: "TeamId",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_google_sync_outbox_teams_TeamId",
                table: "google_sync_outbox",
                column: "TeamId",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_google_sync_outbox_users_UserId",
                table: "google_sync_outbox",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_issue_comments_users_SenderUserId",
                table: "issue_comments",
                column: "SenderUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_issues_users_AssigneeUserId",
                table: "issues",
                column: "AssigneeUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_issues_users_ReporterUserId",
                table: "issues",
                column: "ReporterUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_issues_users_ResolvedByUserId",
                table: "issues",
                column: "ResolvedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_legal_documents_teams_TeamId",
                table: "legal_documents",
                column: "TeamId",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_notification_recipients_users_UserId",
                table: "notification_recipients",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_notifications_users_ResolvedByUserId",
                table: "notifications",
                column: "ResolvedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_role_assignments_users_CreatedByUserId",
                table: "role_assignments",
                column: "CreatedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_role_assignments_users_UserId",
                table: "role_assignments",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_rotas_teams_TeamId",
                table: "rotas",
                column: "TeamId",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_shift_signups_users_EnrolledByUserId",
                table: "shift_signups",
                column: "EnrolledByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_shift_signups_users_ReviewedByUserId",
                table: "shift_signups",
                column: "ReviewedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_shift_signups_users_UserId",
                table: "shift_signups",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_sync_service_settings_users_UpdatedByUserId",
                table: "sync_service_settings",
                column: "UpdatedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_team_join_request_state_history_users_ChangedByUserId",
                table: "team_join_request_state_history",
                column: "ChangedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_team_join_requests_users_ReviewedByUserId",
                table: "team_join_requests",
                column: "ReviewedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_team_join_requests_users_UserId",
                table: "team_join_requests",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_team_members_users_UserId",
                table: "team_members",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_team_role_assignments_users_AssignedByUserId",
                table: "team_role_assignments",
                column: "AssignedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ticket_attendees_users_MatchedUserId",
                table: "ticket_attendees",
                column: "MatchedUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ticket_orders_users_MatchedUserId",
                table: "ticket_orders",
                column: "MatchedUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_volunteer_event_profiles_users_UserId",
                table: "volunteer_event_profiles",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_volunteer_tag_preferences_users_UserId",
                table: "volunteer_tag_preferences",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
