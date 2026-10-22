namespace Humans.Cantina.Services.Dtos;

/// <summary>
/// Deliberately has no <c>MedicalConditions</c>: the cached profile the service reads carries it, and this record is
/// where it stops (GDPR Art. 9). Chips are sets for O(1) per-cell lookups in the matrix.
/// </summary>
internal sealed record DailyPersonRowDto(
    Guid UserId,
    string BurnerName,
    string? DietaryPreference,
    IReadOnlySet<string> Allergies,
    string? AllergyOtherText,
    IReadOnlySet<string> Intolerances,
    string? IntoleranceOtherText);
