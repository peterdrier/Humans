using NodaTime;

namespace Humans.Cantina.Services.Dtos;

/// <summary>
/// The whole page in one payload. Aggregates are over unique humans: a person on site Mon and Wed counts once.
/// </summary>
/// <remarks>
/// <paramref name="WeekStartOffset"/> can be negative (build weeks). Dates and <paramref name="EventName"/> are null with
/// no active event. <paramref name="DietaryBreakdown"/> always has the four preferences plus "Unanswered".
/// <paramref name="AllergyOtherEntries"/> are deduplicated by trimmed text. <paramref name="People"/> is unsorted (the
/// CSV sorts; the page renders none). <paramref name="EventTodayDate"/> comes from the service, not view-side UTC, so a
/// Madrid coordinator late in the evening doesn't see tomorrow highlighted.
/// </remarks>
internal sealed record WeeklyRosterDto(
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
