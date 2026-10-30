using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Grants;

/// <summary>The notification sources this section emits. Names are stored on rows: never change one.</summary>
internal sealed class GrantsNotificationSources : NotificationSourceContributor
{
    /// <summary>A grant entitles the person to something: issued, or the notice resent.</summary>
    public static readonly NotificationSourceDefinition GrantReceived = new("GrantReceived", MessageCategory.System);

    public static readonly NotificationSourceDefinition GrantRevoked = new("GrantRevoked", MessageCategory.System);
}
