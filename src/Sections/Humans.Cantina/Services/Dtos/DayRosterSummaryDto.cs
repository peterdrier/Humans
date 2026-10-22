using NodaTime;

namespace Humans.Cantina.Services.Dtos;

/// <summary>Counts only; per-person detail rolls up across the week in <see cref="WeeklyRosterDto.People"/>.</summary>
/// <remarks><paramref name="CalendarDate"/> is null with no active event.</remarks>
internal sealed record DayRosterSummaryDto(
    int DayOffset,
    LocalDate? CalendarDate,
    int TotalOnSite,
    int UnansweredOnDay);
