using Humans.Application.DTOs.Calendar;
using NodaTime;

namespace Humans.Web.Models.Calendar;

public sealed record CalendarMonthViewModel(
    YearMonth Month,
    IReadOnlyList<CalendarOccurrence> Occurrences,
    Guid? FilterTeamId,
    IReadOnlyList<TeamOption> TeamOptions,
    string ViewerTimezoneLabel);

public sealed record TeamOption(Guid Id, string Name);
