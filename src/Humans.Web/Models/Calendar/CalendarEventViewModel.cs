using Humans.Application.DTOs.Calendar;
using Humans.Domain.Entities;

namespace Humans.Web.Models.Calendar;

public sealed record CalendarEventViewModel(
    CalendarEvent Event,
    string OwningTeamName,
    IReadOnlyList<CalendarOccurrence> UpcomingOccurrences,
    bool CanEdit,
    string ViewerTimezoneLabel);
