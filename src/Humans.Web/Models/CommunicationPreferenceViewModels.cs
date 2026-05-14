using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models;

public class CommunicationPreferencesViewModel
{
    public List<CategoryPreferenceItem> Categories { get; set; } = [];

    /// <summary>
    /// When set, the page is accessed via an unsubscribe token instead of a full session.
    /// The token must be included in AJAX update calls for authorization.
    /// </summary>
    public string? UnsubscribeToken { get; set; }

    /// <summary>
    /// Category encoded in the unsubscribe token, when present. Drives the one-click
    /// "Unsubscribe from <category>" banner and pre-focuses that row in the matrix.
    /// </summary>
    public MessageCategory? HighlightCategory { get; set; }

    /// <summary>
    /// When true, the panel renders values only (no toggles, no POST forms) and exposes
    /// <see cref="CategoryPreferenceItem.UpdateSource"/> / <see cref="CategoryPreferenceItem.UpdatedAt"/>
    /// for admin attribution. False on self-service.
    /// </summary>
    public bool ReadOnly { get; set; }
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

    /// <summary>
    /// Who/what last changed this preference (e.g. "Profile", "MailerLiteSync", "MagicLink").
    /// Surfaced in admin read-only view; null on self-service.
    /// </summary>
    public string? UpdateSource { get; set; }

    /// <summary>
    /// When this preference was last changed. Null if no preference row exists yet
    /// (i.e. the user has never touched this category and we're showing defaults).
    /// </summary>
    public Instant? UpdatedAt { get; set; }
}
