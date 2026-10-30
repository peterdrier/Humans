using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// An action's name is stored on every audit row, so renaming a declared name strands the rows that carry the
/// old one. Every name below has been stored; a section that drops one keeps a definition for it (as a retired
/// action) or ships a data migration first.
/// </summary>
public sealed class AuditActionNameStabilityTests
{
    private static readonly string[] StoredNames =
    [
        "AccountAnonymized", "AccountMergeAccepted", "AccountMergeRejected", "AccountPurged",
        "AgentSettingsUpdated", "AnomalousPermissionDetected", "AssemblyBallotCast", "AssemblyBallotChanged",
        "AssemblyBallotsViewed", "AssemblyVoteCancelled", "AssemblyVoteClosed", "AssemblyVoteExtended",
        "AssemblyVoteOpened", "AssemblyVotePeeked", "AssemblyVoteRemindersSent", "AssemblyVoteRosterMerged",
        "AssemblyVoteStopped", "BackdoorApiKeyIssued", "BackdoorApiKeyRevoked", "CalendarEventCreated",
        "CalendarEventDeleted", "CalendarEventUpdated", "CalendarFeedTokenCreated", "CalendarFeedTokenRotated",
        "CalendarOccurrenceCancelled", "CalendarOccurrenceOverridden", "CampCreated", "CampDeleted",
        "CampEarlyEntryGranted", "CampEarlyEntryRevoked", "CampImageDeleted", "CampImageUploaded",
        "CampMemberAddedByLead", "CampMemberApproved", "CampMemberLeft", "CampMemberRejected", "CampMemberRemoved",
        "CampMemberRequested", "CampMemberWithdrawn", "CampNameChanged", "CampNameLockDateChanged",
        "CampRegistrationClosed", "CampRegistrationOpened", "CampRoleAssigned", "CampRoleDefinitionCreated",
        "CampRoleDefinitionDeactivated", "CampRoleDefinitionReactivated", "CampRoleDefinitionUpdated",
        "CampRoleUnassigned", "CampSeasonApproved", "CampSeasonCreated", "CampSeasonEeSlotCountChanged",
        "CampSeasonRejected", "CampSeasonStatusChanged", "CampSeasonWithdrawn", "CampUpdated", "CampaignActivated",
        "CampaignCodesImported", "CampaignCompleted", "CampaignCreated", "CampaignGrantResent", "CampaignUpdated",
        "CampaignWaveSent", "CampsExported", "CantinaRosterExported", "CityPlanningContainerPlacementClosed",
        "CityPlanningContainerPlacementOpened", "CityPlanningLimitZoneDeleted", "CityPlanningLimitZoneUpdated",
        "CityPlanningOfficialZonesDeleted", "CityPlanningOfficialZonesUpdated", "CityPlanningPlacementClosed",
        "CityPlanningPlacementDatesUpdated", "CityPlanningPlacementOpened", "CityPlanningPlacementOverrideCleared",
        "CityPlanningRegistrationInfoUpdated", "CommunicationPreferenceChanged", "ConsentCheckCleared",
        "ConsentCheckFlagged", "ContactCreated", "ContainerCreated", "ContainerDeleted",
        "ContainerPlacementCleared", "ContainerPlacementNotesUpdated", "ContainerPlacementSaved",
        "ContainerUpdated", "CoordinatorRotaMessageSent", "CoordinatorTeamRotasMessageSent",
        "DataMigrationApplied", "DuplicateAccountFlagged", "EarlyEntryGranted", "EarlyEntryRevoked",
        "EarlyEntryUpdated", "EmailComposerSelfTestSent", "EmailDailySendCountsBackfilled", "EmailOutboxDiscarded",
        "EmailOutboxRetried", "EmailSendingPaused", "EmailSendingResumed", "EventGuideExported",
        "EventSettingsUpdated", "ExpenseApprove", "ExpenseAttachmentRemoved", "ExpenseAttachmentUploaded",
        "ExpenseCategoryOverride", "ExpenseCoordinatorReject", "ExpenseCreatedOnBehalf", "ExpenseEditedOnBehalf",
        "ExpenseEndorse", "ExpenseHoldedFailed", "ExpenseHoldedPushed", "ExpenseHoldedRequeued",
        "ExpenseLineDeclineUndone", "ExpenseLineDeclined", "ExpensePaid", "ExpensePayeeIbanUpdated",
        "ExpenseReject", "ExpenseSepaReopened", "ExpenseSepaSent", "ExpenseSubmit", "ExpenseWithdraw",
        "FacilitatedMessageSent", "FeedbackAssignmentChanged", "FeedbackGitHubLinked", "FeedbackResponseSent",
        "FeedbackStatusChanged", "GateStaffPinReset", "GateStaffPinSet", "GateTerminalPasswordSet",
        "GoogleEmailRenamed", "GoogleResourceAccessGranted", "GoogleResourceAccessRevoked",
        "GoogleResourceDeactivated", "GoogleResourceInheritanceDriftCorrected", "GoogleResourceLinked",
        "GoogleResourceProvisioned", "GoogleResourceSettingsRemediated", "GoogleResourceUnlinked",
        "GoogleResourceUpdated", "GoogleSyncRetryScheduled", "GrantIssued", "GrantNoticeResent",
        "GrantProgramCreated", "GrantProgramEdited", "GrantProgramManagerAdded", "GrantProgramManagerRemoved",
        "GrantRevoked", "HoldedCategoryAccountProvisioned", "HoldedCreditorAccountNumChanged",
        "HoldedCreditorBound", "HoldedCreditorUnbound", "HoldedDuplicateRemoved",
        "HoldedExpenseAccountActiveChanged", "HoldedExpenseAccountCreated", "HoldedExpenseAccountLinked",
        "IbanRemove", "IbanReveal", "IbanSet", "IssueAssigneeChanged", "IssueCreated", "IssueGitHubLinked",
        "IssueSectionChanged", "IssueStatusChanged", "LegacyIdentityEmailBackfilled", "LegalDocumentArchived",
        "LegalDocumentCreated", "LegalDocumentUpdated", "LegalDocumentVersionPublished",
        "LegalDocumentVersionSummaryUpdated", "MailerLiteAudienceSyncCompleted",
        "MailerLiteReconciliationCompleted", "MemberSuspended", "MemberUnsuspended",
        "MembershipsRevokedOnDeletionRequest", "OAuthRenameCollision", "OAuthRenameCollisionBlocked",
        "OrphanUserEmailDeleted", "ParticipationBackfilled", "PreapprovalGrantAmountChanged",
        "PreapprovalGrantApplied", "PreapprovalGrantApproved", "PreapprovalGrantClaimed",
        "PreapprovalGrantConsumed", "PreapprovalGrantDeclined", "PreapprovalGrantIssued",
        "PreapprovalGrantReleased", "PreapprovalGrantRevoked", "PreapprovalMergeDroppedApplication",
        "PreapprovalProgramClosed", "PreapprovalProgramCreated", "PreapprovalProgramEdited",
        "PreapprovalProgramManagerAdded", "PreapprovalProgramManagerRemoved", "PreapprovalReminderSent",
        "RideshareSettingsUpdated", "RoleAssigned", "RoleEnded", "RotaMovedToTeam", "SepaBankLineSkipped",
        "SepaPayoutTransfer", "SepaPayoutTransferBooked", "SepaPayoutTransferWithdrawn", "ShiftSignupBailed",
        "ShiftSignupCancelled", "ShiftSignupConfirmed", "ShiftSignupCreated", "ShiftSignupNoShow",
        "ShiftSignupReassigned", "ShiftSignupRefused", "ShiftSignupVoluntold", "SignupRejected",
        "StoreCounterpartyEdited", "StoreInvoiceIssued", "StoreLineAdded", "StoreLineRemoved", "StoreOrderCreated",
        "StoreOrderDeleted", "StoreOrderYearBackfilled", "StorePaymentDeleted", "StorePaymentExpired",
        "StorePaymentFailed", "StorePaymentMethodBackfilled", "StorePaymentRecorded", "StorePaymentSettled",
        "StorePaymentsReconciled", "StoreProductCreated", "StoreProductDeactivated", "StoreProductPriceChanged",
        "StoreProductUpdated", "StubProfileBackfilled", "SurveyApproved", "SurveyClosed", "SurveyCreated",
        "SurveyInvitesSent", "SurveyOpened", "SurveyRejected", "SurveyReminderSent", "SurveyResponsesExported",
        "SurveySubmittedForApproval", "SurveyUpdated", "SyncModeChanged", "TeamJoinRequestApproved",
        "TeamJoinRequestRejected", "TeamJoinedDirectly", "TeamLeft", "TeamMemberAdded", "TeamMemberRemoved",
        "TeamMemberRoleChanged", "TeamPageContentUpdated", "TeamRoleAssigned", "TeamRoleDefinitionCreated",
        "TeamRoleDefinitionDeleted", "TeamRoleDefinitionUpdated", "TeamRoleUnassigned", "TicketAttendeesExported",
        "TicketContactsImported", "TicketDonationsExported", "TicketOrdersExported", "TicketTransferApproved",
        "TicketTransferAutoFailed", "TicketTransferCancelled", "TicketTransferRejected", "TicketTransferRequested",
        "TierApplicationApproved", "TierApplicationRejected", "TierDowngraded", "TierTermExpiryCorrected",
        "UserEmailAdded", "UserEmailDeleted", "UserEmailDisplacedByOAuthRename", "UserEmailGoogleSet",
        "UserEmailLinked", "UserEmailManuallyVerified", "UserEmailPrimaryCleared", "UserEmailPrimarySet",
        "UserEmailUnlinked", "UserEmailVisibilityChanged", "VolunteerApproved", "VolunteerAvailabilityCleared",
        "VolunteerAvailabilitySet", "VolunteerCampSetupCleared", "VolunteerCampSetupSet", "VolunteerDayOffCleared",
        "VolunteerDayOffMarked", "VolunteerTrackingExported", "WorkgroupApplicationOverdue", "WorkgroupBudgetSet",
        "WorkgroupClosed", "WorkgroupCommentHidden", "WorkgroupCoordinatorsChanged",
        "WorkgroupDispositionRecorded", "WorkgroupLogEntryDeleted", "WorkgroupLogEntryUpdated",
        "WorkgroupMeetingDeleted", "WorkgroupMeetingUpdated", "WorkgroupMemberAdded",
        "WorkgroupMembershipApproved", "WorkgroupMembershipDeclined", "WorkgroupReactivated", "WorkgroupReferred",
        "WorkgroupRefused", "WorkgroupRegistered", "WorkgroupRegisteredExisting", "WorkgroupUpdateDueNotified",
        "WorkgroupWithdrawn", "WorkgroupsRootFolderUpdated", "WorkspaceAccountBackupCodesGenerated",
        "WorkspaceAccountLinked", "WorkspaceAccountPasswordReset", "WorkspaceAccountProvisioned",
        "WorkspaceAccountReactivated", "WorkspaceAccountResetBlockedFor2Sv", "WorkspaceAccountSuspended",
    ];

    [HumansFact]
    public async Task Every_stored_action_name_is_still_declared()
    {
        await using var factory = new NoDatabaseFactory();

        var declared = factory.Services.GetServices<IAuditActionContributor>()
            .SelectMany(c => c.Actions)
            .Select(a => a.Name)
            .ToList();

        declared.Should().Contain(StoredNames);
    }
}
