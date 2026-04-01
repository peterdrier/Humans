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
    TermRenewalReminder = 6
}
