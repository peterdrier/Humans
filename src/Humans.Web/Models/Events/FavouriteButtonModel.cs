namespace Humans.Web.Models.Events;

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
}
