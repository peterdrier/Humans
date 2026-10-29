using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Camps;

/// <summary>The notification sources this section emits. Names are stored on rows: never change one.</summary>
internal sealed class CampsNotificationSources : NotificationSourceContributor
{
    /// <summary>In-app pointer to the email.</summary>
    public static readonly NotificationSourceDefinition FacilitatedMessageReceived = new("FacilitatedMessageReceived", MessageCategory.FacilitatedMessages);

    public static readonly NotificationSourceDefinition CampMembershipApproved = new("CampMembershipApproved", MessageCategory.TeamUpdates);

    public static readonly NotificationSourceDefinition CampMembershipRejected = new("CampMembershipRejected", MessageCategory.TeamUpdates);

    /// <summary>The season the human had requested to join was withdrawn or rejected.</summary>
    public static readonly NotificationSourceDefinition CampMembershipSeasonClosed = new("CampMembershipSeasonClosed", MessageCategory.TeamUpdates);

    public static readonly NotificationSourceDefinition CampRoleAssigned = new("CampRoleAssigned", MessageCategory.TeamUpdates);
}
