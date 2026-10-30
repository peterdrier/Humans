using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Feedback;

/// <summary>The notification sources this section emits. Names are stored on rows: never change one.</summary>
internal sealed class FeedbackNotificationSources : NotificationSourceContributor
{
    public static readonly NotificationSourceDefinition FeedbackResponse = new("FeedbackResponse", MessageCategory.System);
}
