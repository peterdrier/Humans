namespace Humans.Shifts.Contracts;

/// <summary>
/// Render model for <c>_VolunteerProfileBadges</c>. Combines shift-matching data
/// (Skills/Quirks/Languages — from VolunteerEventProfile) with the person's dietary
/// preference + medical conditions (now Profile fields, read via UserInfo).
/// <para><see cref="MedicalConditions"/> is GDPR Art. 9 — it is populated only when
/// the building code has confirmed the viewer holds the MedicalDataViewer policy.
/// The partial additionally guards on <see cref="ShowMedical"/>.</para>
/// <para>Public under <c>Contracts/</c> because Debug's widget gallery constructs it.</para>
/// </summary>
public sealed record VolunteerBadgesViewModel(
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Quirks,
    IReadOnlyList<string> Languages,
    string? DietaryPreference,
    string? MedicalConditions,
    bool ShowMedical);
