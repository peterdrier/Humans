using Humans.Calendar.Services.Dtos;
using NodaTime;

namespace Humans.Calendar.Models;

internal sealed record CalendarMonthViewModel(
    YearMonth Month,
    IReadOnlyList<CalendarOccurrence> Occurrences,
    Guid? FilterTeamId,
    string ViewerTimezoneLabel)
{
    /// <summary>
    /// The viewer's personal iCal subscription URL, rendered as a card below the
    /// month grid. Null on List/Team, which reuse this model but not the card, and
    /// for a viewer with no <c>UserInfo</c> row.
    /// </summary>
    public string? ICalUrl { get; init; }

    /// <summary>
    /// The viewer has no feed token yet: the card offers a button that POSTs to create
    /// one, since rendering the page must not mint a credential.
    /// </summary>
    public bool CanCreateICal { get; init; }
}

internal sealed record TeamOption(Guid Id, string Name);
