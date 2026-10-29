using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Consent;

/// <summary>The notification sources this section emits. Names are stored on rows: never change one.</summary>
internal sealed class ConsentNotificationSources : NotificationSourceContributor
{
    public static readonly NotificationSourceDefinition LegalDocumentPublished = new("LegalDocumentPublished", MessageCategory.System);

    public static readonly NotificationSourceDefinition ReConsentRequired = new("ReConsentRequired", MessageCategory.System);

    /// <summary>Replaced by the consent-review meter; declared so stored rows still filter and purge.</summary>
    public static readonly NotificationSourceDefinition ConsentReviewNeeded = new("ConsentReviewNeeded", MessageCategory.System, NotificationInboxGroup.Approvals, Retired: true);
}
