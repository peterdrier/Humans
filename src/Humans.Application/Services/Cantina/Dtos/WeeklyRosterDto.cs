using NodaTime;

namespace Humans.Application.Services.Cantina.Dtos;

/// <summary>
/// Everything the Cantina Weekly Roster page needs for one event week in
/// a single payload. Computed by <c>ICantinaRosterService</c> so the
/// controller can render headers, aggregate cards, the per-day mini-summary,
/// and the per-human table without further round-trips.
/// </summary>
/// <param name="WeekStartOffset">
/// The day-offset (relative to <c>EventSettings.GateOpeningDate</c>) of the
/// Monday of the week being viewed. Can be negative (build weeks) or
/// positive (strike weeks). Echoed back so the view can label nav links
/// without re-reading state.
/// </param>
/// <param name="WeekStartDate">
/// Calendar date of the week's Monday in the event's timezone. Null when
/// no active event exists.
/// </param>
/// <param name="WeekEndDate">
/// Calendar date of the week's Sunday (<c>WeekStartDate + 6</c>). Null
/// when no active event exists.
/// </param>
/// <param name="EventName">Active event name, or null when no active event exists.</param>
/// <param name="TotalUniqueOnSite">
/// Distinct on-site humans across the week (Mon–Sun). A person on-site
/// multiple days is counted once.
/// </param>
/// <param name="UnansweredCount">
/// Unique humans across the week with no <c>VolunteerEventProfile</c> or
/// with an empty <c>DietaryPreference</c>. Coordinators use this to chase
/// people who haven't filled the form yet.
/// </param>
/// <param name="DietaryBreakdown">
/// Counts keyed by dietary preference, computed over the unique-humans
/// cohort. Always includes the four canonical preferences plus
/// <c>"Unanswered"</c>, even when the count is zero.
/// </param>
/// <param name="AllergyRollup">
/// One row per canonical allergy chip, counted over unique humans. A
/// person with "Peanut" on-site Mon+Wed contributes 1, not 2.
/// </param>
/// <param name="AllergyOtherEntries">
/// Free-text follow-up entries from humans who picked the "Other" allergy
/// chip, <strong>deduplicated</strong> across the week by trimmed text.
/// </param>
/// <param name="IntoleranceRollup">Same shape as <see cref="AllergyRollup"/> for intolerances.</param>
/// <param name="IntoleranceOtherEntries">Same dedup rule as <see cref="AllergyOtherEntries"/>.</param>
/// <param name="Days">
/// Per-day mini-summary, 7 entries Mon..Sun. Each carries that day's
/// distinct on-site count and "no dietary preference" count.
/// </param>
/// <param name="People">
/// One row per unique on-site human across the week. Returned in unspecified
/// order — the web layer's <c>CantinaRosterAssembler</c> sorts for display
/// (first arrival → has-allergies → dietary priority → cultural-collation
/// burner name). Humans with no <c>VolunteerEventProfile</c> still appear
/// here with empty dietary fields.
/// </param>
/// <param name="EventTodayDate">
/// Today's calendar date in the active event's timezone
/// (<c>EventSettings.TimeZoneId</c>). Null when no active event exists.
/// Used by the view to highlight the "today" row in the per-day mini-table
/// — must come from the service, not from view-side <c>DateTime.UtcNow</c>,
/// so a Madrid coordinator viewing late evening doesn't see tomorrow.
/// </param>
public sealed record WeeklyRosterDto(
    int WeekStartOffset,
    LocalDate? WeekStartDate,
    LocalDate? WeekEndDate,
    string? EventName,
    int TotalUniqueOnSite,
    int UnansweredCount,
    IReadOnlyDictionary<string, int> DietaryBreakdown,
    IReadOnlyList<RollupItemDto> AllergyRollup,
    IReadOnlyList<string> AllergyOtherEntries,
    IReadOnlyList<RollupItemDto> IntoleranceRollup,
    IReadOnlyList<string> IntoleranceOtherEntries,
    IReadOnlyList<DayRosterSummaryDto> Days,
    IReadOnlyList<RosterPersonDto> People,
    LocalDate? EventTodayDate);
