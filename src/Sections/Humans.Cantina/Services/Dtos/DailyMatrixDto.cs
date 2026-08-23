using NodaTime;

namespace Humans.Cantina.Services.Dtos;

/// <summary>
/// Per-day "drill-down" payload for the Cantina Daily Matrix page
/// (feature #36 — src/Sections/Humans.Cantina/Docs/features/daily-roster.md). The weekly
/// view's per-day mini-table rows link to this view so cantina
/// coordinators planning a specific meal can see, on a single screen,
/// every on-site human's dietary preference + allergy + intolerance
/// chips as a matrix (rows = people, columns = chips), with column
/// totals at the bottom.
///
/// Computed by <c>ICantinaRosterService.GetDailyRosterAsync</c>; the
/// controller pipes through <c>CantinaRosterAssembler.WithSortedPeople</c>
/// to alphabetize <see cref="People"/> for display. The chip column totals
/// are counted from <see cref="People"/> by the view and the CSV writer —
/// each unique on-site human on this day appears in it exactly once.
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
/// <param name="People">
/// One row per unique on-site human on this day. Returned in unspecified
/// order — the web layer's <c>CantinaRosterAssembler.WithSortedPeople</c>
/// alphabetizes for display. The service builds rows for the whole on-site
/// cohort regardless of profile state; in practice every on-site human has a
/// profile row, so the empty-field path is defensive rather than a case the
/// page is expected to render.
/// </param>
internal sealed record DailyMatrixDto(
    int DayOffset,
    LocalDate? CalendarDate,
    LocalDate? EventTodayDate,
    string? EventName,
    int WeekStartOffset,
    int TotalOnSite,
    int UnansweredCount,
    IReadOnlyList<DailyPersonRowDto> People);
