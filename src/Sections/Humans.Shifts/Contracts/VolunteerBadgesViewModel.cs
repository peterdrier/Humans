namespace Humans.Shifts.Contracts;

/// <summary>
/// Medical conditions are GDPR Art. 9 data and never travel in this model: the partial renders a reveal control when
/// <see cref="HasMedicalConditions"/> and <see cref="CanRevealMedical"/> both hold, and the text comes back from
/// <c>POST /Shifts/Medical/{userId}/Reveal</c>, which audits every reveal. The builder sets both flags only once it has
/// confirmed MedicalDataViewer. Public because widget-gallery sample cards construct it.
/// </summary>
public sealed record VolunteerBadgesViewModel(
    Guid UserId,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Quirks,
    IReadOnlyList<string> Languages,
    string? DietaryPreference,
    bool HasMedicalConditions,
    bool CanRevealMedical);
