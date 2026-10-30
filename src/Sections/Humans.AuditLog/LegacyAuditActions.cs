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

    public static readonly AuditActionDefinition TierApplicationApproved = new("TierApplicationApproved", Verb: "approved tier application for");

    public static readonly AuditActionDefinition TierApplicationRejected = new("TierApplicationRejected", Verb: "rejected tier application for");

    public static readonly AuditActionDefinition TierDowngraded = new("TierDowngraded");

    // From the temporary /Governance/Applications/Admin/TermExpiry screen.
    public static readonly AuditActionDefinition TierTermExpiryCorrected = new("TierTermExpiryCorrected");

    public static readonly AuditActionDefinition CampLeadAdded = new("CampLeadAdded");

    public static readonly AuditActionDefinition CampLeadRemoved = new("CampLeadRemoved");

    public static readonly AuditActionDefinition CampPrimaryLeadTransferred = new("CampPrimaryLeadTransferred");

    public static readonly AuditActionDefinition ShiftSignupConfirmed = new("ShiftSignupConfirmed", Verb: "confirmed signup for", SelfVerb: "signed up for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupRefused = new("ShiftSignupRefused", Verb: "refused signup for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupVoluntold = new("ShiftSignupVoluntold", Verb: "voluntold", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupBailed = new("ShiftSignupBailed", Verb: "bailed", SelfVerb: "bailed from", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupNoShow = new("ShiftSignupNoShow", Verb: "marked no-show for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupCancelled = new("ShiftSignupCancelled", Verb: "removed signup for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition FeedbackResponseSent = new("FeedbackResponseSent");

    public static readonly AuditActionDefinition FeedbackStatusChanged = new("FeedbackStatusChanged");

    // No writer; stored rows may hold it.
    public static readonly AuditActionDefinition WorkspaceAccountBackupCodesInvalidated = new("WorkspaceAccountBackupCodesInvalidated");

    public static readonly AuditActionDefinition AccountMergeRequested = new("AccountMergeRequested");

    public static readonly AuditActionDefinition RotaMovedToTeam = new("RotaMovedToTeam");

    public static readonly AuditActionDefinition FeedbackAssignmentChanged = new("FeedbackAssignmentChanged");

    public static readonly AuditActionDefinition CalendarEventCreated = new("CalendarEventCreated");

    public static readonly AuditActionDefinition CalendarEventUpdated = new("CalendarEventUpdated");

    public static readonly AuditActionDefinition CalendarEventDeleted = new("CalendarEventDeleted");

    public static readonly AuditActionDefinition CalendarOccurrenceCancelled = new("CalendarOccurrenceCancelled");

    public static readonly AuditActionDefinition CalendarOccurrenceOverridden = new("CalendarOccurrenceOverridden");

    public static readonly AuditActionDefinition IssueStatusChanged = new("IssueStatusChanged");

    public static readonly AuditActionDefinition IssueAssigneeChanged = new("IssueAssigneeChanged");

    public static readonly AuditActionDefinition IssueSectionChanged = new("IssueSectionChanged");

    public static readonly AuditActionDefinition IssueGitHubLinked = new("IssueGitHubLinked");

    public static readonly AuditActionDefinition UserEmailProviderBackfilled = new("UserEmailProviderBackfilled");

    public static readonly AuditActionDefinition UserEmailGoogleCleared = new("UserEmailGoogleCleared");

    public static readonly AuditActionDefinition ShiftSignupCreated = new("ShiftSignupCreated", Verb: "created signup for", SelfVerb: "signed up for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupReassigned = new("ShiftSignupReassigned", Verb: "reassigned shift signups for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition StoreOrderCreated = new("StoreOrderCreated");

    public static readonly AuditActionDefinition StoreOrderDeleted = new("StoreOrderDeleted");

    public static readonly AuditActionDefinition StoreLineAdded = new("StoreLineAdded");

    public static readonly AuditActionDefinition StoreLineRemoved = new("StoreLineRemoved");

    public static readonly AuditActionDefinition StoreCounterpartyEdited = new("StoreCounterpartyEdited");

    public static readonly AuditActionDefinition StoreProductCreated = new("StoreProductCreated");

    public static readonly AuditActionDefinition StoreProductUpdated = new("StoreProductUpdated");

    public static readonly AuditActionDefinition StoreProductPriceChanged = new("StoreProductPriceChanged");

    public static readonly AuditActionDefinition StoreProductDeactivated = new("StoreProductDeactivated");

    public static readonly AuditActionDefinition StoreInvoiceIssued = new("StoreInvoiceIssued");

    public static readonly AuditActionDefinition StorePaymentRecorded = new("StorePaymentRecorded");

    public static readonly AuditActionDefinition StorePaymentSettled = new("StorePaymentSettled");

    public static readonly AuditActionDefinition StorePaymentFailed = new("StorePaymentFailed");

    public static readonly AuditActionDefinition StorePaymentExpired = new("StorePaymentExpired");

    public static readonly AuditActionDefinition StorePaymentDeleted = new("StorePaymentDeleted");

    public static readonly AuditActionDefinition TicketTransferRequested = new("TicketTransferRequested");

    public static readonly AuditActionDefinition TicketTransferApproved = new("TicketTransferApproved");

    public static readonly AuditActionDefinition TicketTransferRejected = new("TicketTransferRejected");

    public static readonly AuditActionDefinition TicketTransferCancelled = new("TicketTransferCancelled");

    public static readonly AuditActionDefinition TicketTransferAutoFailed = new("TicketTransferAutoFailed");

    public static readonly AuditActionDefinition GhostExternalLoginsDeleted = new("GhostExternalLoginsDeleted");

    // No writer since the EE start date moved to Settings.
    public static readonly AuditActionDefinition CampSettingsEeStartDateChanged = new("CampSettingsEeStartDateChanged");

    public static readonly AuditActionDefinition MedicalConditionsRevealed = new("MedicalConditionsRevealed");

    public static readonly AuditActionDefinition VolunteerCampSetupSet = new("VolunteerCampSetupSet");

    public static readonly AuditActionDefinition VolunteerCampSetupCleared = new("VolunteerCampSetupCleared");

    // No writer since the day-off redesign; stored rows may hold it.
    public static readonly AuditActionDefinition VolunteerDayBlocked = new("VolunteerDayBlocked");

    public static readonly AuditActionDefinition VolunteerDayUnblocked = new("VolunteerDayUnblocked");

    public static readonly AuditActionDefinition VolunteerOwnBlockedDaysSaved = new("VolunteerOwnBlockedDaysSaved");

    public static readonly AuditActionDefinition VolunteerDayOffMarked = new("VolunteerDayOffMarked");

    public static readonly AuditActionDefinition VolunteerDayOffCleared = new("VolunteerDayOffCleared");

    public static readonly AuditActionDefinition MailerLiteReconciliationCompleted = new("MailerLiteReconciliationCompleted");

    public static readonly AuditActionDefinition MailerLiteAudienceSyncCompleted = new("MailerLiteAudienceSyncCompleted");

    public static readonly AuditActionDefinition TicketContactsImported = new("TicketContactsImported");

    // Buyer names and amounts, for the accountant.
    public static readonly AuditActionDefinition TicketDonationsExported = new("TicketDonationsExported");

    public static readonly AuditActionDefinition ContainerCreated = new("ContainerCreated");

    public static readonly AuditActionDefinition ContainerUpdated = new("ContainerUpdated");

    public static readonly AuditActionDefinition ContainerDeleted = new("ContainerDeleted");

    public static readonly AuditActionDefinition ContainerPlacementSaved = new("ContainerPlacementSaved");

    public static readonly AuditActionDefinition ContainerPlacementCleared = new("ContainerPlacementCleared");

    public static readonly AuditActionDefinition ContainerPlacementNotesUpdated = new("ContainerPlacementNotesUpdated");

    public static readonly AuditActionDefinition CoordinatorRotaMessageSent = new("CoordinatorRotaMessageSent");

    public static readonly AuditActionDefinition CoordinatorTeamRotasMessageSent = new("CoordinatorTeamRotasMessageSent");

    // On a volunteer's behalf, from the Profile build strip.
    public static readonly AuditActionDefinition VolunteerAvailabilitySet = new("VolunteerAvailabilitySet");

    public static readonly AuditActionDefinition VolunteerAvailabilityCleared = new("VolunteerAvailabilityCleared");

    public static readonly AuditActionDefinition StorePaymentsReconciled = new("StorePaymentsReconciled");

    public static readonly AuditActionDefinition GateTerminalPasswordSet = new("GateTerminalPasswordSet");

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

    // Machine API only, where the filer and the reporter can differ. In-app creation is recorded on the issue row.
    public static readonly AuditActionDefinition IssueCreated = new("IssueCreated");

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

    // Workgroups: the daily job's flags and notices are audited too.
    public static readonly AuditActionDefinition WorkgroupRegistered = new("WorkgroupRegistered");

    public static readonly AuditActionDefinition WorkgroupReferred = new("WorkgroupReferred");

    public static readonly AuditActionDefinition WorkgroupRefused = new("WorkgroupRefused");

    public static readonly AuditActionDefinition WorkgroupWithdrawn = new("WorkgroupWithdrawn");

    public static readonly AuditActionDefinition WorkgroupClosed = new("WorkgroupClosed");

    public static readonly AuditActionDefinition WorkgroupReactivated = new("WorkgroupReactivated");

    public static readonly AuditActionDefinition WorkgroupCoordinatorsChanged = new("WorkgroupCoordinatorsChanged");

    public static readonly AuditActionDefinition WorkgroupDispositionRecorded = new("WorkgroupDispositionRecorded");

    // Bootstrapping: a group that already existed, registered backdated.
    public static readonly AuditActionDefinition WorkgroupRegisteredExisting = new("WorkgroupRegisteredExisting");

    // Budget allocation and Holded account set, changed or cleared.
    public static readonly AuditActionDefinition WorkgroupBudgetSet = new("WorkgroupBudgetSet");

    // The daily job set DormantSince after sixty days' silence.
    public static readonly AuditActionDefinition WorkgroupDormancyFlagged = new("WorkgroupDormancyFlagged");

    // The daily job notified the Board that a flagged group is a close candidate.
    public static readonly AuditActionDefinition WorkgroupCloseCandidateFlagged = new("WorkgroupCloseCandidateFlagged");

    // The daily job notified the Board that an application passed its fourteenth day.
    public static readonly AuditActionDefinition WorkgroupApplicationOverdue = new("WorkgroupApplicationOverdue");

    // The daily job nudged the coordinators that the monthly update is due.
    public static readonly AuditActionDefinition WorkgroupUpdateDueNotified = new("WorkgroupUpdateDueNotified");

    // The log is a working record: the audit trail is where a deletion stays visible.
    public static readonly AuditActionDefinition WorkgroupLogEntryDeleted = new("WorkgroupLogEntryDeleted");

    // Overwritten in place: the audit trail is the only record of the earlier text.
    public static readonly AuditActionDefinition WorkgroupLogEntryUpdated = new("WorkgroupLogEntryUpdated");

    public static readonly AuditActionDefinition WorkgroupMeetingUpdated = new("WorkgroupMeetingUpdated");

    // Soft delete: the row stays.
    public static readonly AuditActionDefinition WorkgroupMeetingDeleted = new("WorkgroupMeetingDeleted");

    public static readonly AuditActionDefinition WorkgroupCommentHidden = new("WorkgroupCommentHidden");

    public static readonly AuditActionDefinition WorkgroupsRootFolderUpdated = new("WorkgroupsRootFolderUpdated");

    public static readonly AuditActionDefinition WorkgroupMembershipApproved = new("WorkgroupMembershipApproved");

    public static readonly AuditActionDefinition WorkgroupMembershipDeclined = new("WorkgroupMembershipDeclined");

    // Added directly, without a join request.
    public static readonly AuditActionDefinition WorkgroupMemberAdded = new("WorkgroupMemberAdded");

    public static readonly AuditActionDefinition AssemblyVoteOpened = new("AssemblyVoteOpened");

    public static readonly AuditActionDefinition AssemblyVoteStopped = new("AssemblyVoteStopped");

    public static readonly AuditActionDefinition AssemblyVoteExtended = new("AssemblyVoteExtended");

    public static readonly AuditActionDefinition AssemblyVoteCancelled = new("AssemblyVoteCancelled");

    public static readonly AuditActionDefinition AssemblyVoteClosed = new("AssemblyVoteClosed");

    public static readonly AuditActionDefinition AssemblyVotePeeked = new("AssemblyVotePeeked");

    public static readonly AuditActionDefinition AssemblyBallotCast = new("AssemblyBallotCast");

    public static readonly AuditActionDefinition AssemblyBallotChanged = new("AssemblyBallotChanged");

    public static readonly AuditActionDefinition AssemblyBallotsViewed = new("AssemblyBallotsViewed");

    public static readonly AuditActionDefinition AssemblyVoteRosterMerged = new("AssemblyVoteRosterMerged");

    public static readonly AuditActionDefinition AssemblyVoteRemindersSent = new("AssemblyVoteRemindersSent");

    // An Update or a meeting that has started cleared a workgroup's dormancy inquiry.
    public static readonly AuditActionDefinition WorkgroupDormancyCleared = new("WorkgroupDormancyCleared");

    // A Store correction path resolved a legacy order's persisted year from its camp season.
    public static readonly AuditActionDefinition StoreOrderYearBackfilled = new("StoreOrderYearBackfilled");

    // Every successful SaveEventSettingsAsync, including the Settings carry screen's reconcile writes.
    public static readonly AuditActionDefinition EventSettingsUpdated = new("EventSettingsUpdated");

    // A Store admin's repair run copied a legacy payment's int Method into its string MethodName column.
    public static readonly AuditActionDefinition StorePaymentMethodBackfilled = new("StorePaymentMethodBackfilled");

    public static readonly AuditActionDefinition CampaignCreated = new("CampaignCreated");

    public static readonly AuditActionDefinition CampaignUpdated = new("CampaignUpdated");

    public static readonly AuditActionDefinition CampaignActivated = new("CampaignActivated");

    public static readonly AuditActionDefinition CampaignCompleted = new("CampaignCompleted");

    public static readonly AuditActionDefinition CampaignCodesImported = new("CampaignCodesImported");

    public static readonly AuditActionDefinition CampaignWaveSent = new("CampaignWaveSent");

    public static readonly AuditActionDefinition CampaignGrantResent = new("CampaignGrantResent");

    public static readonly AuditActionDefinition TicketAttendeesExported = new("TicketAttendeesExported");

    public static readonly AuditActionDefinition TicketOrdersExported = new("TicketOrdersExported");

    public static readonly AuditActionDefinition SurveyResponsesExported = new("SurveyResponsesExported");

    public static readonly AuditActionDefinition UserNamesBackfilled = new("UserNamesBackfilled");

    public static readonly AuditActionDefinition ParticipationBackfilled = new("ParticipationBackfilled");

    public static readonly AuditActionDefinition PreapprovalProgramCreated = new("PreapprovalProgramCreated");

    public static readonly AuditActionDefinition PreapprovalProgramEdited = new("PreapprovalProgramEdited");

    public static readonly AuditActionDefinition PreapprovalProgramClosed = new("PreapprovalProgramClosed");

    public static readonly AuditActionDefinition PreapprovalProgramManagerAdded = new("PreapprovalProgramManagerAdded");

    public static readonly AuditActionDefinition PreapprovalProgramManagerRemoved = new("PreapprovalProgramManagerRemoved");

    public static readonly AuditActionDefinition PreapprovalGrantIssued = new("PreapprovalGrantIssued");

    public static readonly AuditActionDefinition PreapprovalGrantApplied = new("PreapprovalGrantApplied");

    public static readonly AuditActionDefinition PreapprovalGrantApproved = new("PreapprovalGrantApproved");

    public static readonly AuditActionDefinition PreapprovalGrantDeclined = new("PreapprovalGrantDeclined");

    public static readonly AuditActionDefinition PreapprovalGrantRevoked = new("PreapprovalGrantRevoked");

    public static readonly AuditActionDefinition PreapprovalGrantClaimed = new("PreapprovalGrantClaimed");

    public static readonly AuditActionDefinition PreapprovalGrantReleased = new("PreapprovalGrantReleased");

    public static readonly AuditActionDefinition PreapprovalGrantConsumed = new("PreapprovalGrantConsumed");

    public static readonly AuditActionDefinition PreapprovalGrantAmountChanged = new("PreapprovalGrantAmountChanged");

    public static readonly AuditActionDefinition PreapprovalReminderSent = new("PreapprovalReminderSent");

    public static readonly AuditActionDefinition PreapprovalMergeDroppedApplication = new("PreapprovalMergeDroppedApplication");

    public static readonly AuditActionDefinition CalendarFeedTokenCreated = new("CalendarFeedTokenCreated");

    public static readonly AuditActionDefinition CalendarFeedTokenRotated = new("CalendarFeedTokenRotated");

    public static readonly AuditActionDefinition AgentSettingsUpdated = new("AgentSettingsUpdated");

    public static readonly AuditActionDefinition EventGuideExported = new("EventGuideExported");

    public static readonly AuditActionDefinition CantinaRosterExported = new("CantinaRosterExported");

    public static readonly AuditActionDefinition CantinaHealthDetailsRevealed = new("CantinaHealthDetailsRevealed");

    public static readonly AuditActionDefinition VolunteerTrackingExported = new("VolunteerTrackingExported");

    public static readonly AuditActionDefinition DataMigrationApplied = new("DataMigrationApplied");

    public static readonly AuditActionDefinition GrantProgramCreated = new("GrantProgramCreated");

    public static readonly AuditActionDefinition GrantProgramEdited = new("GrantProgramEdited");

    public static readonly AuditActionDefinition GrantProgramManagerAdded = new("GrantProgramManagerAdded");

    public static readonly AuditActionDefinition GrantProgramManagerRemoved = new("GrantProgramManagerRemoved");

    public static readonly AuditActionDefinition GrantIssued = new("GrantIssued");

    public static readonly AuditActionDefinition GrantRevoked = new("GrantRevoked");

    public static readonly AuditActionDefinition GrantNoticeResent = new("GrantNoticeResent");

}
