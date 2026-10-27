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
    private static CategoryPreferenceItem FromPreference(
        MessageCategory category, CommunicationPreferenceSnapshot? preference, IReadOnlySet<MessageCategory> lockedCategories)
    {
        var locked = lockedCategories.Contains(category);
        var editable = !category.IsAlwaysOn() && !locked;
        return new CategoryPreferenceItem
        {
            Category = category,
            // Missing Marketing rows default off, matching the send policy.
            EmailEnabled = preference is null ? !category.DefaultOptedOut() : !preference.OptedOut,
            AlertEnabled = preference?.InboxEnabled ?? true,
            EmailEditable = editable,
            AlertEditable = editable,
            IsLocked = locked,
        };
    }

    public MessageCategory Category { get; set; }

    /// <summary>True means the member receives email: the inverse of <c>OptedOut</c>.</summary>
    public bool EmailEnabled { get; set; } = true;

    public bool AlertEnabled { get; set; } = true;

    /// <summary>False for always-on categories and for locked ones.</summary>
    public bool EmailEditable { get; set; }

    public bool AlertEditable { get; set; }

    public bool IsLocked { get; set; }

    /// <summary>Shown only in the read-only view.</summary>
    public string? UpdateSource { get; set; }

    /// <summary>Null when no row exists and the defaults are shown.</summary>
    public Instant? UpdatedAt { get; set; }

    /// <summary>One item per active category, in display order.</summary>
    public static List<CategoryPreferenceItem> ForActiveCategories(
        IReadOnlyList<CommunicationPreferenceSnapshot> prefs, IReadOnlySet<MessageCategory> lockedCategories)
    {
        var prefsByCategory = prefs.ToDictionary(p => p.Category);
        return MessageCategoryExtensions.ActiveCategories.Select(category =>
        {
            var pref = prefsByCategory.GetValueOrDefault(category);
            var item = FromPreference(category, pref, lockedCategories);
            item.UpdateSource = pref?.UpdateSource;
            item.UpdatedAt = pref?.UpdatedAt;
            return item;
        }).ToList();
    }
}
