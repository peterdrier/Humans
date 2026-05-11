using Humans.Application.DTOs.Calendar;
using Humans.Application.Interfaces.Calendar;

namespace Humans.Web.Models.Calendar;

public sealed record CalendarEventViewModel(
    CalendarEventInfo Event,
    string OwningTeamName,
    IReadOnlyList<CalendarOccurrence> UpcomingOccurrences,
    bool CanEdit,
    string ViewerTimezoneLabel);
