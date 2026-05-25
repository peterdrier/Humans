using NodaTime;

namespace Humans.Application.Services.Cantina.Dtos;

/// <summary>
/// Per-day "drill-down" payload for the Cantina Daily Matrix page
/// (feature #36 — docs/features/cantina/daily-roster.md). The weekly
/// view's per-day mini-table rows link to this view so cantina
/// coordinators planning a specific meal can see, on a single screen,
/// every on-site human's dietary preference + allergy + intolerance
/// chips as a matrix (rows = people, columns = chips), with column
/// totals at the bottom.
///
/// Computed by <c>ICantinaRosterService.GetDailyRosterAsync</c>; the
/// controller pipes through <c>CantinaRosterAssembler.WithSortedPeople</c>
/// to alphabetize <see cref="People"/> for display. Day-scoped semantics:
/// every aggregate (<see cref="DietaryBreakdown"/>, <see cref="AllergyRollup"/>,
/// etc.) counts each unique on-site human on this day once.
/// </summary>
/// <param name="DayOffset">
/// The day-offset relative to <c>EventSettings.GateOpeningDate</c>.
/// Echoed back so the view can build prev/next nav and the CSV link
/// without re-reading state.
/// </param>
/// <param name="CalendarDate">
/// <c>GateOpeningDate + DayOffset</c> in the event's timezone. Null when
/// no active event exists.
/// </param>
/// <param name="EventTodayDate">
/// Today's calendar date in the active event's timezone
/// (<c>EventSettings.TimeZoneId</c>). Null when no active event exists.
/// Used by the view to render a "Today" badge in the header when
/// <see cref="CalendarDate"/> matches.
/// </param>
/// <param name="EventName">Active event name, or null when no active event exists.</param>
/// <param name="WeekStartOffset">
/// The Monday-of-week (relative to <c>GateOpeningDate</c>) containing
/// <see cref="CalendarDate"/>. Used to render the "back to weekly" link.
/// Falls back to <c>DayOffset - ((DayOffset % 7 + 7) % 7)</c> when no
/// active event exists (so the link still resolves to a valid week).
/// </param>
/// <param name="TotalOnSite">Distinct on-site humans on this single day.</param>
/// <param name="UnansweredCount">
/// On-site humans on this single day whose <c>DietaryPreference</c> is
/// null/empty.
/// </param>
/// <param name="DietaryBreakdown">
/// Counts keyed by dietary preference, computed over this day's cohort.
/// Always includes the four canonical preferences plus <c>"Unanswered"</c>.
/// </param>
/// <param name="AllergyRollup">
/// One row per canonical allergy chip, counted over the day's cohort.
/// </param>
/// <param name="AllergyOtherEntries">
/// Free-text entries from humans who picked the "Other" allergy chip on
/// this day, deduplicated by trimmed text.
/// </param>
/// <param name="IntoleranceRollup">Same shape as <see cref="AllergyRollup"/> for intolerances.</param>
/// <param name="IntoleranceOtherEntries">Same dedup rule as <see cref="AllergyOtherEntries"/>.</param>
/// <param name="People">
/// One row per unique on-site human on this day. Returned in unspecified
/// order — the web layer's <c>CantinaRosterAssembler.WithSortedPeople</c>
/// alphabetizes for display. Humans with no <c>VolunteerEventProfile</c>
/// still appear here with empty dietary fields.
/// </param>
public sealed record DailyMatrixDto(
    int DayOffset,
    LocalDate? CalendarDate,
    LocalDate? EventTodayDate,
    string? EventName,
    int WeekStartOffset,
    int TotalOnSite,
    int UnansweredCount,
    IReadOnlyDictionary<string, int> DietaryBreakdown,
    IReadOnlyList<RollupItemDto> AllergyRollup,
    IReadOnlyList<string> AllergyOtherEntries,
    IReadOnlyList<RollupItemDto> IntoleranceRollup,
    IReadOnlyList<string> IntoleranceOtherEntries,
    IReadOnlyList<DailyPersonRowDto> People);
