using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Notifications;

/// <summary>
/// Sources stored by sections that no longer exist. Declared retired here so the nightly purge still clears
/// their unresolved rows after the owning section is deleted.
/// </summary>
internal sealed class RetiredNotificationSources : NotificationSourceContributor
{
    // Feedback, removed in favour of Issues.
    public static readonly NotificationSourceDefinition FeedbackResponse = new("FeedbackResponse", MessageCategory.System, Retired: true);
}
