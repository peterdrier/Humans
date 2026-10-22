namespace Humans.Events.Contracts;

/// <summary>
/// The JS contract between <c>_FavouriteButton.cshtml</c> and <c>wwwroot/js/site.js</c>: the toggle goes through the
/// JSON API without a reload, so filters and scroll position survive.
/// </summary>
public sealed class FavouriteButtonModel
{
    public required Guid EventId { get; init; }

    /// <summary>Null = whole-event favourite.</summary>
    public int? DayOffset { get; init; }

    public bool IsFavourited { get; init; }

    /// <summary>
    /// My Schedule: remove the row on un-favourite (after <see cref="ConfirmMessage"/>) instead of flipping the heart.
    /// </summary>
    public bool RemoveRow { get; init; }

    public string? ConfirmMessage { get; init; }

    public required string AddTitle { get; init; }

    public required string RemoveTitle { get; init; }

    /// <summary>Used instead of <see cref="RemoveTitle"/> when <see cref="RemoveRow"/>.</summary>
    public required string RemoveRowTitle { get; init; }

    public required string ErrorMessage { get; init; }
}
