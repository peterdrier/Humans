using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Rideshare;

/// <summary>The notification sources this section emits. Names are stored on rows: never change one.</summary>
internal sealed class RideshareNotificationSources : NotificationSourceContributor
{
    /// <summary>Someone expressed interest in a ride offer, or a driver answered a ride request.</summary>
    public static readonly NotificationSourceDefinition RideshareInterestReceived = new("RideshareInterestReceived", MessageCategory.System);

    public static readonly NotificationSourceDefinition RideshareInterestAccepted = new("RideshareInterestAccepted", MessageCategory.System);

    /// <summary>The posting owner declined a rideshare interest (neutral wording, no reason).</summary>
    public static readonly NotificationSourceDefinition RideshareInterestDeclined = new("RideshareInterestDeclined", MessageCategory.System);
}
