using Humans.Calendar.Services.Dtos;

namespace Humans.Calendar.Models;

internal sealed record CalendarEventViewModel(
    CalendarEventDetail Event,
    string OwningTeamName,
    IReadOnlyList<CalendarOccurrence> UpcomingOccurrences,
    bool CanEdit,
    string ViewerTimezoneLabel);
