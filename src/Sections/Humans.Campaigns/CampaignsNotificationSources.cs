using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Campaigns;

/// <summary>The notification sources this section emits. Names are stored on rows: never change one.</summary>
internal sealed class CampaignsNotificationSources : NotificationSourceContributor
{
    /// <summary>In-app echo of a campaign code grant.</summary>
    public static readonly NotificationSourceDefinition CampaignReceived = new("CampaignReceived", MessageCategory.Marketing);
}
