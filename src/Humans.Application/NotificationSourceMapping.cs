using Humans.Domain.Enums;

namespace Humans.Application;

/// <summary>
/// Maps NotificationSource to MessageCategory for preference checks.
/// </summary>
public static class NotificationSourceMapping
{
    public static MessageCategory ToMessageCategory(this NotificationSource source) => source switch
    {
        NotificationSource.TeamMemberAdded => MessageCategory.TeamUpdates,
        NotificationSource.ShiftCoverageGap => MessageCategory.VolunteerUpdates,
        NotificationSource.ShiftSignupChange => MessageCategory.VolunteerUpdates,
        NotificationSource.ConsentReviewNeeded => MessageCategory.System,
        NotificationSource.ApplicationSubmitted => MessageCategory.Governance,
        NotificationSource.ApplicationApproved => MessageCategory.Governance,
        NotificationSource.ApplicationRejected => MessageCategory.Governance,
        NotificationSource.VolunteerApproved => MessageCategory.Governance,
        NotificationSource.ProfileRejected => MessageCategory.System,
        NotificationSource.AccessSuspended => MessageCategory.System,
        NotificationSource.ReConsentRequired => MessageCategory.System,
        NotificationSource.TeamJoinRequestSubmitted => MessageCategory.TeamUpdates,
        NotificationSource.TeamJoinRequestDecided => MessageCategory.TeamUpdates,
        NotificationSource.FeedbackResponse => MessageCategory.System,
        NotificationSource.WorkspaceCredentialsReady => MessageCategory.System,
        NotificationSource.SyncError => MessageCategory.System,
        NotificationSource.TermRenewalReminder => MessageCategory.System,
        _ => MessageCategory.System
    };
}
