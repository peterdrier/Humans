using Humans.Application.DTOs.Calendar;

namespace Humans.Web.Models.Calendar;

public sealed record CalendarEventViewModel(
    CalendarEventDetail Event,
    string OwningTeamName,
    IReadOnlyList<CalendarOccurrence> UpcomingOccurrences,
    bool CanEdit,
    string ViewerTimezoneLabel);
