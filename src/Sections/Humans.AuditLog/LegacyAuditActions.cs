using Humans.AuditLog.Contracts;

namespace Humans.AuditLog;

/// <summary>
/// Transitional: every action the <see cref="AuditAction"/> enum still carries, declared here until its own section
/// declares it on a <see cref="AuditActionContributor"/> of its own. A section that moves its actions deletes them from
/// this class in the same PR (the registry fails startup on a name declared twice). Deleted with the enum.
/// </summary>
internal sealed class LegacyAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition RoleAssigned = new("RoleAssigned", Verb: "assigned role to", DescriptionIsTail: true, QuickFilter: "Roles");

    public static readonly AuditActionDefinition RoleEnded = new("RoleEnded", Verb: "ended role for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition CampLeadAdded = new("CampLeadAdded");

    public static readonly AuditActionDefinition CampLeadRemoved = new("CampLeadRemoved");

    public static readonly AuditActionDefinition CampPrimaryLeadTransferred = new("CampPrimaryLeadTransferred");

    public static readonly AuditActionDefinition FeedbackResponseSent = new("FeedbackResponseSent");

    public static readonly AuditActionDefinition FeedbackStatusChanged = new("FeedbackStatusChanged");

    // No writer; stored rows may hold it.
    public static readonly AuditActionDefinition WorkspaceAccountBackupCodesInvalidated = new("WorkspaceAccountBackupCodesInvalidated");

    public static readonly AuditActionDefinition AccountMergeRequested = new("AccountMergeRequested");

    public static readonly AuditActionDefinition FeedbackAssignmentChanged = new("FeedbackAssignmentChanged");

    public static readonly AuditActionDefinition CalendarEventCreated = new("CalendarEventCreated");

    public static readonly AuditActionDefinition CalendarEventUpdated = new("CalendarEventUpdated");

    public static readonly AuditActionDefinition CalendarEventDeleted = new("CalendarEventDeleted");

    public static readonly AuditActionDefinition CalendarOccurrenceCancelled = new("CalendarOccurrenceCancelled");

    public static readonly AuditActionDefinition CalendarOccurrenceOverridden = new("CalendarOccurrenceOverridden");

    public static readonly AuditActionDefinition UserEmailProviderBackfilled = new("UserEmailProviderBackfilled");

    public static readonly AuditActionDefinition UserEmailGoogleCleared = new("UserEmailGoogleCleared");

    public static readonly AuditActionDefinition GhostExternalLoginsDeleted = new("GhostExternalLoginsDeleted");

    // No writer since the EE start date moved to Settings.
    public static readonly AuditActionDefinition CampSettingsEeStartDateChanged = new("CampSettingsEeStartDateChanged");

    // No writer since the day-off redesign; stored rows may hold it.
    public static readonly AuditActionDefinition VolunteerDayBlocked = new("VolunteerDayBlocked");

    public static readonly AuditActionDefinition VolunteerDayUnblocked = new("VolunteerDayUnblocked");

    public static readonly AuditActionDefinition VolunteerOwnBlockedDaysSaved = new("VolunteerOwnBlockedDaysSaved");

    public static readonly AuditActionDefinition MailerLiteReconciliationCompleted = new("MailerLiteReconciliationCompleted");

    public static readonly AuditActionDefinition MailerLiteAudienceSyncCompleted = new("MailerLiteAudienceSyncCompleted");

    public static readonly AuditActionDefinition ContainerCreated = new("ContainerCreated");

    public static readonly AuditActionDefinition ContainerUpdated = new("ContainerUpdated");

    public static readonly AuditActionDefinition ContainerDeleted = new("ContainerDeleted");

    public static readonly AuditActionDefinition ContainerPlacementSaved = new("ContainerPlacementSaved");

    public static readonly AuditActionDefinition ContainerPlacementCleared = new("ContainerPlacementCleared");

    public static readonly AuditActionDefinition ContainerPlacementNotesUpdated = new("ContainerPlacementNotesUpdated");

    public static readonly AuditActionDefinition GateStaffPinSet = new("GateStaffPinSet");

    public static readonly AuditActionDefinition GateStaffPinReset = new("GateStaffPinReset");

    public static readonly AuditActionDefinition SurveyCreated = new("SurveyCreated");

    public static readonly AuditActionDefinition SurveyUpdated = new("SurveyUpdated");

    public static readonly AuditActionDefinition SurveyOpened = new("SurveyOpened");

    public static readonly AuditActionDefinition SurveyClosed = new("SurveyClosed");

    public static readonly AuditActionDefinition SurveyInvitesSent = new("SurveyInvitesSent");

    public static readonly AuditActionDefinition SurveyReminderSent = new("SurveyReminderSent");

    // An author submits a Draft for Board/Admin review. Approval opens and sends in one step; rejection returns
    // it to Draft with a note.
    public static readonly AuditActionDefinition SurveySubmittedForApproval = new("SurveySubmittedForApproval");

    public static readonly AuditActionDefinition SurveyApproved = new("SurveyApproved");

    public static readonly AuditActionDefinition SurveyRejected = new("SurveyRejected");

    // Rotation is a revoke of the old key followed by an issue of the new one.
    public static readonly AuditActionDefinition BackdoorApiKeyIssued = new("BackdoorApiKeyIssued");

    public static readonly AuditActionDefinition BackdoorApiKeyRevoked = new("BackdoorApiKeyRevoked");

    public static readonly AuditActionDefinition FeedbackGitHubLinked = new("FeedbackGitHubLinked");

    // Every City Planning settings write is audited: the settings row records when a phase changed, not who.
    // Opened/Closed force the barrio placement schedule; OverrideCleared hands placement back to it.
    public static readonly AuditActionDefinition CityPlanningPlacementOpened = new("CityPlanningPlacementOpened");

    public static readonly AuditActionDefinition CityPlanningPlacementClosed = new("CityPlanningPlacementClosed");

    public static readonly AuditActionDefinition CityPlanningPlacementOverrideCleared = new("CityPlanningPlacementOverrideCleared");

    public static readonly AuditActionDefinition CityPlanningContainerPlacementOpened = new("CityPlanningContainerPlacementOpened");

    public static readonly AuditActionDefinition CityPlanningContainerPlacementClosed = new("CityPlanningContainerPlacementClosed");

    public static readonly AuditActionDefinition CityPlanningLimitZoneUpdated = new("CityPlanningLimitZoneUpdated");

    public static readonly AuditActionDefinition CityPlanningLimitZoneDeleted = new("CityPlanningLimitZoneDeleted");

    public static readonly AuditActionDefinition CityPlanningOfficialZonesUpdated = new("CityPlanningOfficialZonesUpdated");

    public static readonly AuditActionDefinition CityPlanningOfficialZonesDeleted = new("CityPlanningOfficialZonesDeleted");

    // The barrio placement schedule and the barrio registration page copy.
    public static readonly AuditActionDefinition CityPlanningPlacementDatesUpdated = new("CityPlanningPlacementDatesUpdated");

    public static readonly AuditActionDefinition CityPlanningRegistrationInfoUpdated = new("CityPlanningRegistrationInfoUpdated");

    public static readonly AuditActionDefinition RideshareSettingsUpdated = new("RideshareSettingsUpdated");

    // The daily job set DormantSince after sixty days' silence.
    public static readonly AuditActionDefinition WorkgroupDormancyFlagged = new("WorkgroupDormancyFlagged");

    // The daily job notified the Board that a flagged group is a close candidate.
    public static readonly AuditActionDefinition WorkgroupCloseCandidateFlagged = new("WorkgroupCloseCandidateFlagged");

    // An Update or a meeting that has started cleared a workgroup's dormancy inquiry.
    public static readonly AuditActionDefinition WorkgroupDormancyCleared = new("WorkgroupDormancyCleared");

    // Every successful SaveEventSettingsAsync, including the Settings carry screen's reconcile writes.
    public static readonly AuditActionDefinition EventSettingsUpdated = new("EventSettingsUpdated");

    public static readonly AuditActionDefinition CampaignCreated = new("CampaignCreated");

    public static readonly AuditActionDefinition CampaignUpdated = new("CampaignUpdated");

    public static readonly AuditActionDefinition CampaignActivated = new("CampaignActivated");

    public static readonly AuditActionDefinition CampaignCompleted = new("CampaignCompleted");

    public static readonly AuditActionDefinition CampaignCodesImported = new("CampaignCodesImported");

    public static readonly AuditActionDefinition CampaignWaveSent = new("CampaignWaveSent");

    public static readonly AuditActionDefinition CampaignGrantResent = new("CampaignGrantResent");

    public static readonly AuditActionDefinition SurveyResponsesExported = new("SurveyResponsesExported");

    public static readonly AuditActionDefinition UserNamesBackfilled = new("UserNamesBackfilled");

    public static readonly AuditActionDefinition CalendarFeedTokenCreated = new("CalendarFeedTokenCreated");

    public static readonly AuditActionDefinition CalendarFeedTokenRotated = new("CalendarFeedTokenRotated");

    public static readonly AuditActionDefinition AgentSettingsUpdated = new("AgentSettingsUpdated");

    public static readonly AuditActionDefinition EventGuideExported = new("EventGuideExported");

    public static readonly AuditActionDefinition CantinaRosterExported = new("CantinaRosterExported");

    public static readonly AuditActionDefinition CantinaHealthDetailsRevealed = new("CantinaHealthDetailsRevealed");

    public static readonly AuditActionDefinition DataMigrationApplied = new("DataMigrationApplied");

    public static readonly AuditActionDefinition GrantProgramCreated = new("GrantProgramCreated");

    public static readonly AuditActionDefinition GrantProgramEdited = new("GrantProgramEdited");

    public static readonly AuditActionDefinition GrantProgramManagerAdded = new("GrantProgramManagerAdded");

    public static readonly AuditActionDefinition GrantProgramManagerRemoved = new("GrantProgramManagerRemoved");

    public static readonly AuditActionDefinition GrantIssued = new("GrantIssued");

    public static readonly AuditActionDefinition GrantRevoked = new("GrantRevoked");

    public static readonly AuditActionDefinition GrantNoticeResent = new("GrantNoticeResent");

}
