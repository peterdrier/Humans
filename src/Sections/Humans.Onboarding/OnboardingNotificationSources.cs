using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Onboarding;

/// <summary>The notification sources this section emits. Names are stored on rows: never change one.</summary>
internal sealed class OnboardingNotificationSources : NotificationSourceContributor
{
    public static readonly NotificationSourceDefinition ProfileRejected = new("ProfileRejected", MessageCategory.System);
}
