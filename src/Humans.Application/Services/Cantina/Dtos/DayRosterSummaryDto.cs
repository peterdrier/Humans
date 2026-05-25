using NodaTime;

namespace Humans.Application.Services.Cantina.Dtos;

/// <summary>
/// One row of the Cantina Weekly Roster's per-day mini-summary table.
/// Carries counts only — the per-person detail rolls up across the whole
/// week in <see cref="WeeklyRosterDto.People"/> rather than being repeated
/// per day.
/// </summary>
/// <param name="DayOffset">
/// The day-offset relative to <c>EventSettings.GateOpeningDate</c>.
/// </param>
/// <param name="CalendarDate">
/// <c>GateOpeningDate + DayOffset</c> in the event's timezone. Null when
/// no active event exists.
/// </param>
/// <param name="TotalOnSite">Distinct on-site humans on this single day.</param>
/// <param name="UnansweredOnDay">
/// Distinct on-site humans on this single day whose <c>DietaryPreference</c>
/// is null/empty (no <c>VolunteerEventProfile</c> counts the same as a VEP
/// with no preference set).
/// </param>
public sealed record DayRosterSummaryDto(
    int DayOffset,
    LocalDate? CalendarDate,
    int TotalOnSite,
    int UnansweredOnDay);
