using Humans.Calendar.Services.Dtos;
using NodaTime;

namespace Humans.Calendar.Models;

internal sealed record CalendarMonthViewModel(
    YearMonth Month,
    IReadOnlyList<CalendarOccurrence> Occurrences,
    Guid? FilterTeamId,
    IReadOnlyList<TeamOption> TeamOptions,
    string ViewerTimezoneLabel);

internal sealed record TeamOption(Guid Id, string Name);
