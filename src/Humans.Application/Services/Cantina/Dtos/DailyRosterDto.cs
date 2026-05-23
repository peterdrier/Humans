using NodaTime;

namespace Humans.Application.Services.Cantina.Dtos;

/// <summary>
/// Everything the Cantina Daily Roster page needs for one event day in
/// a single payload. Computed by <c>ICantinaRosterService</c> so the
/// controller can render headers, aggregate cards, and the per-human
/// table without further round-trips.
/// </summary>
/// <param name="DayOffset">
/// The event day index requested by the caller (0 = gate opening).
/// Echoed back so the view can label day-pickers without re-reading
/// state.
/// </param>
/// <param name="CalendarDate">
/// <c>EventSettings.GateOpeningDate + DayOffset</c> in the event's
/// timezone. Null when no active event exists.
/// </param>
/// <param name="EventName">Active event name, or null when no active event exists.</param>
/// <param name="TotalOnSite">Distinct on-site humans for the day.</param>
/// <param name="UnansweredCount">
/// On-site humans without a <c>VolunteerEventProfile</c> or with an
/// empty <c>DietaryPreference</c>. The Cantina coordinator uses this
/// to nudge people who haven't filled the form yet.
/// </param>
/// <param name="DietaryBreakdown">
/// Counts keyed by dietary preference. Always includes the four
/// canonical preferences plus <c>"Unanswered"</c>, even when the
/// count is zero.
/// </param>
/// <param name="AllergyRollup">
/// One row per canonical allergy chip in
/// <see cref="Humans.Domain.Constants.DietaryOptions.AllergyOptions"/>,
/// with the count of on-site humans who ticked it. The <c>"Other"</c>
/// row counts how many chose Other; the free-text entries themselves
/// are in <see cref="AllergyOtherEntries"/>.
/// </param>
/// <param name="AllergyOtherEntries">
/// Free-text follow-up text entered by humans who picked the "Other"
/// allergy chip. Filtered to non-empty values; preserves duplicates so
/// the coordinator sees frequency.
/// </param>
/// <param name="IntoleranceRollup">Same shape as <see cref="AllergyRollup"/> for intolerances.</param>
/// <param name="IntoleranceOtherEntries">Same shape as <see cref="AllergyOtherEntries"/> for intolerances.</param>
/// <param name="People">
/// One row per on-site human, ordered alphabetically by
/// <see cref="RosterPersonDto.BurnerName"/> (ordinal). Humans with
/// no <c>VolunteerEventProfile</c> still appear here with empty
/// dietary fields.
/// </param>
public sealed record DailyRosterDto(
    int DayOffset,
    LocalDate? CalendarDate,
    string? EventName,
    int TotalOnSite,
    int UnansweredCount,
    IReadOnlyDictionary<string, int> DietaryBreakdown,
    IReadOnlyList<RollupItemDto> AllergyRollup,
    IReadOnlyList<string> AllergyOtherEntries,
    IReadOnlyList<RollupItemDto> IntoleranceRollup,
    IReadOnlyList<string> IntoleranceOtherEntries,
    IReadOnlyList<RosterPersonDto> People);
