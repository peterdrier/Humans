using NodaTime;

namespace Humans.Cantina.Services.Dtos;

/// <summary>
/// Each unique on-site human on the day appears once in <see cref="People"/> (unspecified order; the controller sorts),
/// and shared column counts serve the view and CSV writer.
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
    IReadOnlyList<DailyPersonRowDto> People)
{
    internal int CountDietaryPreference(string preference) =>
        People.Count(person => string.Equals(person.DietaryPreference, preference, StringComparison.Ordinal));

    internal int CountAllergy(string allergy) =>
        People.Count(person => person.Allergies.Contains(allergy));

    internal int CountIntolerance(string intolerance) =>
        People.Count(person => person.Intolerances.Contains(intolerance));
}
