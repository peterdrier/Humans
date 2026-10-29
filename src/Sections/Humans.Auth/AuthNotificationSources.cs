using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Auth;

/// <summary>The notification sources this section emits. Names are stored on rows: never change one.</summary>
internal sealed class AuthNotificationSources : NotificationSourceContributor
{
    public static readonly NotificationSourceDefinition RoleAssignmentChanged = new("RoleAssignmentChanged", MessageCategory.Governance);
}
