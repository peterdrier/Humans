using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Governance;

/// <summary>The notification sources this section emits. Names are stored on rows: never change one.</summary>
internal sealed class GovernanceNotificationSources : NotificationSourceContributor
{
    public static readonly NotificationSourceDefinition TermRenewalReminder = new("TermRenewalReminder", MessageCategory.System);

    public static readonly NotificationSourceDefinition ApplicationApproved = new("ApplicationApproved", MessageCategory.Governance, NotificationInboxGroup.Approvals);

    public static readonly NotificationSourceDefinition ApplicationRejected = new("ApplicationRejected", MessageCategory.Governance, NotificationInboxGroup.Approvals);

    public static readonly NotificationSourceDefinition AssemblyVoteOpened = new("AssemblyVoteOpened", MessageCategory.Governance);

    /// <summary>Replaced by the Board voting meter; declared so stored rows still filter and purge.</summary>
    public static readonly NotificationSourceDefinition ApplicationSubmitted = new("ApplicationSubmitted", MessageCategory.Governance, NotificationInboxGroup.Approvals, Retired: true);
}
