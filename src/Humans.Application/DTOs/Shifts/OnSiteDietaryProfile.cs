namespace Humans.Application.DTOs.Shifts;

/// <summary>
/// Cross-section read-model for a single on-site volunteer's dietary data,
/// projected from <c>VolunteerEventProfile</c> by the Shifts service for the
/// Cantina roster (feature #36). Deliberately omits
/// <c>VolunteerEventProfile.MedicalConditions</c>: medical data (GDPR Art. 9)
/// never leaves the Shifts service, so the cantina never receives it.
/// </summary>
public sealed record OnSiteDietaryProfile(
    Guid UserId,
    string? DietaryPreference,
    IReadOnlyList<string> Allergies,
    string? AllergyOtherText,
    IReadOnlyList<string> Intolerances,
    string? IntoleranceOtherText);
