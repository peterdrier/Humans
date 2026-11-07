namespace Humans.Shifts.Contracts;

/// <summary>
/// Shifts knows nothing about a member's medical conditions (GDPR Art. 9, owned by Users). When
/// <see cref="CanRevealMedical"/> holds (the viewer passes <c>UsersPolicies.MedicalDataViewer</c>) the partial renders a
/// reveal control that posts to Users' audited <c>POST /Profile/{id}/Medical/Reveal</c>. Public because widget-gallery
/// sample cards construct it.
/// </summary>
public sealed record VolunteerBadgesViewModel(
    Guid UserId,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Quirks,
    IReadOnlyList<string> Languages,
    string? DietaryPreference,
    bool CanRevealMedical);
