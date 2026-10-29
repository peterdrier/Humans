using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Preapprovals;

/// <summary>The notification sources this section emits. Names are stored on rows: never change one.</summary>
internal sealed class PreapprovalsNotificationSources : NotificationSourceContributor
{
    /// <summary>A pre-approval the person can file an expense report against: issued, released, deadline moved, or a reminder. Resolved when the grant leaves Open.</summary>
    public static readonly NotificationSourceDefinition PreapprovalFileBy = new("PreapprovalFileBy", MessageCategory.System);

    /// <summary>Someone applied to a pre-approval program; its managers decide. Resolved on approve or decline.</summary>
    public static readonly NotificationSourceDefinition PreapprovalApplicationReceived = new("PreapprovalApplicationReceived", MessageCategory.System);

    /// <summary>A pre-approval was declined, revoked, or had its amount changed.</summary>
    public static readonly NotificationSourceDefinition PreapprovalGrantDecided = new("PreapprovalGrantDecided", MessageCategory.System);
}
