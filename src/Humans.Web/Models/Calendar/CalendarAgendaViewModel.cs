using Humans.Application.DTOs.Calendar;
using NodaTime;

namespace Humans.Web.Models.Calendar;

public sealed record CalendarAgendaViewModel(
    Instant FromUtc,
    Instant ToUtc,
    IReadOnlyList<CalendarOccurrence> Occurrences,
    Guid? FilterTeamId,
    string ViewerTimezoneLabel);
