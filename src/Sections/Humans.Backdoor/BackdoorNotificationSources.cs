using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Backdoor;

/// <summary>The notification sources this section emits. Names are stored on rows: never change one.</summary>
internal sealed class BackdoorNotificationSources : NotificationSourceContributor
{
    /// <summary>System: a new way into someone's account is security news, so it is never switched off.</summary>
    public static readonly NotificationSourceDefinition ConnectionApproved = new("BackdoorConnectionApproved", MessageCategory.System);
}
