using Humans.Calendar.Services.Dtos;
using NodaTime;

namespace Humans.Calendar.Models;

internal sealed record CalendarMonthViewModel(
    YearMonth Month,
    IReadOnlyList<CalendarOccurrence> Occurrences,
    Guid? FilterTeamId,
    string ViewerTimezoneLabel)
{
    /// <summary>Null on List and Team, which reuse this model without the card, and for a viewer with no UserInfo.</summary>
    public string? ICalUrl { get; init; }

    /// <summary>No token yet: the card offers a create button, since rendering the page must not mint one.</summary>
    public bool CanCreateICal { get; init; }
}

internal sealed record TeamOption(Guid Id, string Name);
