namespace Humans.Shifts.Contracts;

/// <summary>
/// Render model for <c>_VolunteerProfileBadges</c>. Combines shift-matching data
/// (Skills/Quirks/Languages — from VolunteerEventProfile) with the person's dietary
/// preference + medical conditions (now Profile fields, read via UserInfo).
/// <para><see cref="MedicalConditions"/> is GDPR Art. 9 — it is populated only when
/// the building code has confirmed the viewer holds the MedicalDataViewer policy.
/// The partial additionally guards on <see cref="ShowMedical"/>.</para>
/// <para>Under <c>Contracts/</c> rather than <c>Models/</c> because Shell's widget gallery
/// binds it (HUM0034 — a section type is internal by default). Moved out of
/// <c>Humans.UI</c> at G5 lane 4b-i, nobodies-collective/Humans#866.</para>
/// </summary>
public sealed record VolunteerBadgesViewModel(
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Quirks,
    IReadOnlyList<string> Languages,
    string? DietaryPreference,
    string? MedicalConditions,
    bool ShowMedical);
