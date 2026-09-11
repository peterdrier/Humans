using NodaTime;

namespace Humans.Shifts.Domain;

/// <summary>
/// User-scoped volunteer shift profile: skills, quirks, and languages used for
/// shift-matching. One-to-one with User. (Dietary + medical moved to Profile.)
/// </summary>
internal sealed class VolunteerEventProfile
{
    public Guid Id { get; init; }

    /// <summary>
    /// FK to the volunteer (1:1). Settable so the account-merge fold
    /// (<c>IShiftManagementService.ReassignProfilesAndTagPrefsToUserAsync</c>)
    /// can re-FK rows from a source user to the merge target.
    /// </summary>
    public Guid UserId { get; set; }

    public List<string> Skills { get; set; } = [];

    public List<string> Quirks { get; set; } = [];

    public List<string> Languages { get; set; } = [];

    // Dietary + medical MOVED to Profile (see the dietary-medical-to-profile
    // migration). These columns are RETAINED but unused — the data was backfilled
    // to Profile and all code now reads/writes Profile. Per
    // memory/architecture/no-drops-until-prod-verified.md they are dropped in a
    // follow-up PR after prod soak. Do NOT read or write these.

    // Use Profile.DietaryPreference.
    public string? DietaryPreference { get; set; }

    // Use Profile.Allergies.
    public List<string> Allergies { get; set; } = [];

    // Use Profile.Intolerances.
    public List<string> Intolerances { get; set; } = [];

    // Use Profile.AllergyOtherText.
    public string? AllergyOtherText { get; set; }

    // Use Profile.IntoleranceOtherText.
    public string? IntoleranceOtherText { get; set; }

    // Use Profile.MedicalConditions.
    public string? MedicalConditions { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }
}
