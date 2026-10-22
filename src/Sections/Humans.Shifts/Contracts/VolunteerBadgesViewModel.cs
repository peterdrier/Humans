namespace Humans.Shifts.Contracts;

/// <summary>
/// <see cref="MedicalConditions"/> is GDPR Art. 9: set only once the builder has confirmed MedicalDataViewer, and the
/// partial also guards on <see cref="ShowMedical"/>. Public because Debug's widget gallery constructs it.
/// </summary>
public sealed record VolunteerBadgesViewModel(
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Quirks,
    IReadOnlyList<string> Languages,
    string? DietaryPreference,
    string? MedicalConditions,
    bool ShowMedical);
