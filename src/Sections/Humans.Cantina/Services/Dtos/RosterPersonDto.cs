using NodaTime;

namespace Humans.Cantina.Services.Dtos;

/// <summary>
/// Deliberately has no <c>MedicalConditions</c>: the cached profile the service reads carries it, and this record is
/// where it stops. Every person has at least one on-site day in the week, so <paramref name="ArrivesOn"/> is
/// non-nullable.
/// </summary>
/// <remarks>
/// <paramref name="ArrivesOn"/> is the first on-site day in the window, the true arrival only when it falls this week.
/// <paramref name="NoShift"/> is days with no signup, not days off site: the human may be around, working informally.
/// </remarks>
internal sealed record RosterPersonDto(
    Guid UserId,
    string BurnerName,
    LocalDate ArrivesOn,
    IReadOnlyList<LocalDate> NoShift,
    string? DietaryPreference,
    IReadOnlyList<string> Allergies,
    string? AllergyOtherText,
    IReadOnlyList<string> Intolerances,
    string? IntoleranceOtherText);
