namespace Humans.Domain.Enums;

/// <summary>
/// Source system that generated a notification.
/// Each source maps to a MessageCategory for preference checks.
/// </summary>
public enum NotificationSource
{
    /// <summary>Team member was added to a team.</summary>
    TeamMemberAdded = 0,

    /// <summary>Shift coverage gap detected.</summary>
    ShiftCoverageGap = 1,

    /// <summary>Shift signup change (confirm, bail, cancel).</summary>
    ShiftSignupChange = 2,

    /// <summary>Consent review needed for a new human.</summary>
    ConsentReviewNeeded = 3,

    /// <summary>Tier application submitted for Board review.</summary>
    ApplicationSubmitted = 4,

    /// <summary>Google sync or resource provisioning error.</summary>
    SyncError = 5,

    /// <summary>Term renewal reminder for Colaborador/Asociado.</summary>
    TermRenewalReminder = 6,

    /// <summary>Tier application approved by the Board.</summary>
    ApplicationApproved = 7,

    /// <summary>Tier application rejected by the Board.</summary>
    ApplicationRejected = 8,

    /// <summary>Volunteer approved (profile cleared for membership).</summary>
    VolunteerApproved = 9,

    /// <summary>Profile/signup rejected during onboarding review.</summary>
    ProfileRejected = 10,

    /// <summary>Member access suspended for non-compliance.</summary>
    AccessSuspended = 11,

    /// <summary>New document version requires re-consent.</summary>
    ReConsentRequired = 12,

    /// <summary>Team join request submitted (notifies coordinators).</summary>
    TeamJoinRequestSubmitted = 13,

    /// <summary>Team join request approved or rejected (notifies requester).</summary>
    TeamJoinRequestDecided = 14,

    /// <summary>Admin responded to a feedback report.</summary>
    FeedbackResponse = 15,

    /// <summary>Workspace (@nobodies.team) credentials are ready.</summary>
    WorkspaceCredentialsReady = 16,

    /// <summary>A governance role was assigned or ended for a user.</summary>
    RoleAssignmentChanged = 18,

    /// <summary>Campaign code was granted to a user (in-app echo).</summary>
    CampaignReceived = 19,

    /// <summary>User was removed from a team.</summary>
    TeamMemberRemoved = 20,

    /// <summary>User was enrolled (voluntold) for a shift by a coordinator.</summary>
    ShiftAssigned = 21,

    /// <summary>Google reconciliation detected and fixed drift.</summary>
    GoogleDriftDetected = 22,

    /// <summary>Facilitated message was sent (in-app pointer to check email).</summary>
    FacilitatedMessageReceived = 23,

    /// <summary>A new legal document version was published.</summary>
    LegalDocumentPublished = 24
}
