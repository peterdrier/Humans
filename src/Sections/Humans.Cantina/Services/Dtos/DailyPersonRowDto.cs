namespace Humans.Cantina.Services.Dtos;

/// <summary>
/// Chips are sets for O(1) per-cell lookups in the matrix.
/// </summary>
internal sealed record DailyPersonRowDto(
    Guid UserId,
    string BurnerName,
    string? DietaryPreference,
    IReadOnlySet<string> Allergies,
    string? AllergyOtherText,
    IReadOnlySet<string> Intolerances,
    string? IntoleranceOtherText);
