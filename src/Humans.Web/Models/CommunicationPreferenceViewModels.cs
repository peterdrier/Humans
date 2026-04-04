using Humans.Domain.Enums;

namespace Humans.Web.Models;

public class CommunicationPreferencesViewModel
{
    public List<CategoryPreferenceItem> Categories { get; set; } = [];
}

public class CategoryPreferenceItem
{
    public MessageCategory Category { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Positive framing: true = user receives email for this category.
    /// Stored as !OptedOut in the entity.
    /// </summary>
    public bool EmailEnabled { get; set; } = true;

    /// <summary>
    /// Whether in-app alerts are enabled for this category.
    /// Maps directly to InboxEnabled on the entity.
    /// </summary>
    public bool AlertEnabled { get; set; } = true;

    /// <summary>
    /// Whether the user can change the email preference for this category.
    /// False for always-on categories (System, CampaignCodes) and locked ticketing.
    /// </summary>
    public bool EmailEditable { get; set; }

    /// <summary>
    /// Whether the user can change the alert preference for this category.
    /// </summary>
    public bool AlertEditable { get; set; }

    /// <summary>
    /// Optional note shown below the category (e.g., "Locked — you have a ticket order for 2026").
    /// </summary>
    public string? Note { get; set; }
}
