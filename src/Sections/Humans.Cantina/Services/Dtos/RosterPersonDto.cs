using NodaTime;

namespace Humans.Cantina.Services.Dtos;

/// <summary>One person on the cantina weekly roster.</summary>
/// <remarks>
/// <paramref name="ArrivesOn"/> is the true arrival day, the day before the first confirmed shift of the event, even
/// when it falls before this week.
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
