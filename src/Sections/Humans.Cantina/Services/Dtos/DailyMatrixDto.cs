using NodaTime;

namespace Humans.Cantina.Services.Dtos;

/// <summary>
/// Each unique on-site human on the day appears once in <see cref="People"/> (unspecified order; the controller sorts),
/// and the view and CSV writer count the chip totals from it.
/// </summary>
/// <remarks>
/// <paramref name="CalendarDate"/>, <paramref name="EventTodayDate"/> and <paramref name="EventName"/> are null with no
/// active event; <paramref name="WeekStartOffset"/> then falls back to offset arithmetic so the back link still resolves.
/// </remarks>
internal sealed record DailyMatrixDto(
    int DayOffset,
    LocalDate? CalendarDate,
    LocalDate? EventTodayDate,
    string? EventName,
    int WeekStartOffset,
    int TotalOnSite,
    int UnansweredCount,
    IReadOnlyList<DailyPersonRowDto> People);
