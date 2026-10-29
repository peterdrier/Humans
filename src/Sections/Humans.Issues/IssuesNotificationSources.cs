using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Issues;

/// <summary>The notification sources this section emits. Names are stored on rows: never change one.</summary>
internal sealed class IssuesNotificationSources : NotificationSourceContributor
{
    public static readonly NotificationSourceDefinition IssueComment = new("IssueComment", MessageCategory.System);

    /// <summary>Notifies the reporter and assignee.</summary>
    public static readonly NotificationSourceDefinition IssueStatusChanged = new("IssueStatusChanged", MessageCategory.System);

    public static readonly NotificationSourceDefinition IssueAssigned = new("IssueAssigned", MessageCategory.System);

    /// <summary>Notifies admins and the section's role-holders.</summary>
    public static readonly NotificationSourceDefinition IssueSubmitted = new("IssueSubmitted", MessageCategory.System);
}
