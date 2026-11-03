using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Feedback;

/// <summary>Kept declared and retired so the inbox purge clears the rows Feedback already wrote.</summary>
internal sealed class FeedbackNotificationSources : NotificationSourceContributor
{
    public static readonly NotificationSourceDefinition FeedbackResponse = new("FeedbackResponse", MessageCategory.System, Retired: true);
}
