using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Teams;

/// <summary>The notification sources this section emits. Names are stored on rows: never change one.</summary>
internal sealed class TeamsNotificationSources : NotificationSourceContributor
{
    public static readonly NotificationSourceDefinition TeamMemberAdded = new("TeamMemberAdded", MessageCategory.TeamUpdates);

    public static readonly NotificationSourceDefinition TeamMemberRemoved = new("TeamMemberRemoved", MessageCategory.TeamUpdates);

    /// <summary>Notifies the team's coordinators.</summary>
    public static readonly NotificationSourceDefinition TeamJoinRequestSubmitted = new("TeamJoinRequestSubmitted", MessageCategory.TeamUpdates, NotificationInboxGroup.Approvals);

    /// <summary>Notifies the requester.</summary>
    public static readonly NotificationSourceDefinition TeamJoinRequestDecided = new("TeamJoinRequestDecided", MessageCategory.TeamUpdates, NotificationInboxGroup.Approvals);
}
