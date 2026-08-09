namespace Humans.UI.Models;

/// <summary>
/// Drives the instant favourite heart shared by Browse, the events card, and
/// My Schedule. Single source of the JS contract rendered by
/// <c>_FavouriteButton.cshtml</c> and handled by <c>wwwroot/js/site.js</c>:
/// the button toggles the favourite through the JSON API without reloading the
/// page, so the user's filters and scroll position survive.
/// </summary>
public sealed class FavouriteButtonModel
{
    public required Guid EventId { get; init; }

    /// <summary>Day offset the heart toggles; null = whole-event favourite.</summary>
    public int? DayOffset { get; init; }

    public bool IsFavourited { get; init; }

    /// <summary>
    /// My Schedule: remove the row on un-favourite (after <see cref="ConfirmMessage"/>)
    /// instead of flipping the heart in place. Renders the broken-heart icon.
    /// </summary>
    public bool RemoveRow { get; init; }

    /// <summary>Optional confirm prompt shown before acting (My Schedule removal).</summary>
    public string? ConfirmMessage { get; init; }

    // Labels are supplied by the caller rather than looked up in the partial. The strings
    // are Events' (Events_AddToFavourites and friends) and live in EventsResource since
    // nobodies-collective/Humans#866 G5, but this model and its partial are Humans.UI —
    // Base, which must not reference a section. Each caller localizes from the resource
    // set it already has, and the partial stays resource-neutral for the next section
    // that needs a favourite heart.

    /// <summary>Title/ARIA label for "add to favourites".</summary>
    public required string AddTitle { get; init; }

    /// <summary>Title/ARIA label for "remove from favourites".</summary>
    public required string RemoveTitle { get; init; }

    /// <summary>Title/ARIA label used instead of <see cref="RemoveTitle"/> when <see cref="RemoveRow"/>.</summary>
    public required string RemoveRowTitle { get; init; }

    /// <summary>Message surfaced by the JS when the toggle request fails.</summary>
    public required string ErrorMessage { get; init; }
}
