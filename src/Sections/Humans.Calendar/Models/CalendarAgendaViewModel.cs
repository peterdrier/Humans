using Humans.Calendar.Services.Dtos;
using NodaTime;

namespace Humans.Calendar.Models;

internal sealed record CalendarAgendaViewModel(
    Instant FromUtc,
    Instant ToUtc,
    IReadOnlyList<CalendarOccurrence> Occurrences,
    Guid? FilterTeamId,
    string ViewerTimezoneLabel);
