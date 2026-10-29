using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Users;

/// <summary>The notification sources this section emits. Names are stored on rows: never change one.</summary>
internal sealed class UsersNotificationSources : NotificationSourceContributor
{
    /// <summary>Admin or non-compliance suspension; resolved when access is restored.</summary>
    public static readonly NotificationSourceDefinition AccessSuspended = new("AccessSuspended", MessageCategory.System);
}
