namespace Humans.Notifications.Contracts;

/// <summary>Stored by name: never rename a value. Each maps to a <c>MessageCategory</c> for preference checks.</summary>
public enum NotificationSource
{
    TeamMemberAdded = 0,

    ShiftCoverageGap = 1,

    /// <summary>Confirm, bail or cancel.</summary>
    ShiftSignupChange = 2,

    ConsentReviewNeeded = 3,

    ApplicationSubmitted = 4,

    /// <summary>Google sync or resource provisioning error.</summary>
    SyncError = 5,

    TermRenewalReminder = 6,

    ApplicationApproved = 7,

    ApplicationRejected = 8,

    VolunteerApproved = 9,

    ProfileRejected = 10,

    AccessSuspended = 11,

    ReConsentRequired = 12,

    /// <summary>Notifies the team's coordinators.</summary>
    TeamJoinRequestSubmitted = 13,

    /// <summary>Notifies the requester.</summary>
    TeamJoinRequestDecided = 14,

    FeedbackResponse = 15,

    WorkspaceCredentialsReady = 16,

    RoleAssignmentChanged = 18,

    /// <summary>In-app echo of a campaign code grant.</summary>
    CampaignReceived = 19,

    TeamMemberRemoved = 20,

    /// <summary>Enrolled (voluntold) by a coordinator.</summary>
    ShiftAssigned = 21,

    /// <summary>Google reconciliation detected and fixed drift.</summary>
    GoogleDriftDetected = 22,

    /// <summary>In-app pointer to the email.</summary>
    FacilitatedMessageReceived = 23,

    LegalDocumentPublished = 24,

    CampMembershipApproved = 25,

    CampMembershipRejected = 26,

    /// <summary>The season the human had requested to join was withdrawn or rejected.</summary>
    CampMembershipSeasonClosed = 27,

    CampRoleAssigned = 28,

    IssueComment = 29,

    /// <summary>Notifies the reporter and assignee.</summary>
    IssueStatusChanged = 30,

    IssueAssigned = 31,

    /// <summary>Notifies admins and the section's role-holders.</summary>
    IssueSubmitted = 32,

    /// <summary>Someone expressed interest in a ride offer, or a driver answered a ride request.</summary>
    RideshareInterestReceived = 33,

    RideshareInterestAccepted = 34,

    /// <summary>The posting owner declined a rideshare interest (neutral wording, no reason).</summary>
    RideshareInterestDeclined = 35,

    AssemblyVoteOpened = 36,

    /// <summary>A working group applied, was referred, or the Board owes it a decision.</summary>
    WorkgroupRegistrationPending = 37,

    /// <summary>A working group's registration was decided: registered, refused, withdrawn, ended or reactivated.</summary>
    WorkgroupRegistrationDecided = 38,

    /// <summary>The reporting rhythm: monthly update due, dormancy inquiry, or a status update requested.</summary>
    WorkgroupReportingDue = 39,

    /// <summary>A working group published a document, opened comments, answered a comment, or delivered.</summary>
    WorkgroupDocumentActivity = 40,

    /// <summary>The Board recorded its written disposition on a delivered document.</summary>
    WorkgroupDispositionRecorded = 41,

    /// <summary>Someone asked to join a working group; its coordinators decide.</summary>
    WorkgroupMembershipRequested = 42,

    /// <summary>A join request was approved or declined, or a coordinator added the person directly.</summary>
    WorkgroupMembershipDecided = 43,

    /// <summary>A pre-approval the person can file an expense report against: issued, released, deadline moved, or a reminder. Resolved when the grant leaves Open.</summary>
    PreapprovalFileBy = 44,

    /// <summary>Someone applied to a pre-approval program; its managers decide. Resolved on approve or decline.</summary>
    PreapprovalApplicationReceived = 45,

    /// <summary>A pre-approval was declined, revoked, or had its amount changed.</summary>
    PreapprovalGrantDecided = 46,

    /// <summary>A grant entitles the person to something: issued, or the notice resent.</summary>
    GrantReceived = 47,

    GrantRevoked = 48,
}
