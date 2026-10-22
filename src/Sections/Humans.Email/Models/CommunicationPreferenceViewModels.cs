using Humans.Email.Contracts;
using Humans.Email.Services;
using NodaTime;

namespace Humans.Email.Models;

internal sealed class CommunicationPreferencesViewModel
{
    public List<CategoryPreferenceItem> Categories { get; set; } = [];

    /// <summary>Set when the page is reached through an unsubscribe token instead of a session; update calls must send it back.</summary>
    public string? UnsubscribeToken { get; set; }

    /// <summary>The category in the unsubscribe token: drives the one-click banner and focuses that row.</summary>
    public MessageCategory? HighlightCategory { get; set; }

    /// <summary>Admin view: values only, no forms, with who changed each row and when.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Current year, shown on the Ticketing row's label.</summary>
    public int TicketingYear { get; set; }
}

internal sealed class CategoryPreferenceItem
{
    public MessageCategory Category { get; set; }

    /// <summary>True means the member receives email: the inverse of <c>OptedOut</c>.</summary>
    public bool EmailEnabled { get; set; } = true;

    public bool AlertEnabled { get; set; } = true;

    /// <summary>False for always-on categories and for locked Ticketing.</summary>
    public bool EmailEditable { get; set; }

    public bool AlertEditable { get; set; }

    public bool IsTicketingLocked { get; set; }

    /// <summary>Shown only in the read-only view.</summary>
    public string? UpdateSource { get; set; }

    /// <summary>Null when no row exists and the defaults are shown.</summary>
    public Instant? UpdatedAt { get; set; }

    /// <summary>One item per active category, in display order.</summary>
    public static List<CategoryPreferenceItem> ForActiveCategories(
        IReadOnlyList<CommunicationPreferenceSnapshot> prefs, bool hasTicketOrder)
    {
        var prefsByCategory = prefs.ToDictionary(p => p.Category);
        return MessageCategoryExtensions.ActiveCategories.Select(category =>
        {
            var pref = prefsByCategory.GetValueOrDefault(category);
            var isTicketingLocked = CommunicationPreferenceService.IsTicketingLocked(category, hasTicketOrder);
            var editable = !category.IsAlwaysOn() && !isTicketingLocked;
            return new CategoryPreferenceItem
            {
                Category = category,
                // No row: the category's default, the same rule the send path applies.
                EmailEnabled = pref is null ? !category.DefaultOptedOut() : !pref.OptedOut,
                AlertEnabled = pref?.InboxEnabled ?? true,
                EmailEditable = editable,
                AlertEditable = editable,
                IsTicketingLocked = isTicketingLocked,
                UpdateSource = pref?.UpdateSource,
                UpdatedAt = pref?.UpdatedAt,
            };
        }).ToList();
    }
}
