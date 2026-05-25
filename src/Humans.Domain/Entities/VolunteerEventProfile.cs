using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// User-scoped volunteer shift profile: skills, quirks, and languages used for
/// shift-matching. One-to-one with User. (Dietary + medical moved to Profile —
/// see docs/superpowers/specs/2026-05-25-dietary-medical-to-profile-design.md.)
/// </summary>
public class VolunteerEventProfile
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// FK to the volunteer (1:1). Settable so the account-merge fold
    /// (<c>IShiftManagementService.ReassignProfilesAndTagPrefsToUserAsync</c>)
    /// can re-FK rows from a source user to the merge target.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Volunteer's self-reported skills.
    /// </summary>
    public List<string> Skills { get; set; } = [];

    /// <summary>
    /// Personality quirks / working style notes.
    /// </summary>
    public List<string> Quirks { get; set; } = [];

    /// <summary>
    /// Languages spoken.
    /// </summary>
    public List<string> Languages { get; set; } = [];

    // Dietary + medical MOVED to Profile (see the dietary-medical-to-profile
    // migration). These columns are RETAINED but unused — the data was backfilled
    // to Profile and all code now reads/writes Profile. Per
    // memory/architecture/no-drops-until-prod-verified.md they are dropped in a
    // follow-up PR after prod soak. Do NOT read or write these.

    /// <summary>RETAINED for prod-soak drop. Use Profile.DietaryPreference.</summary>
    public string? DietaryPreference { get; set; }

    /// <summary>RETAINED for prod-soak drop. Use Profile.Allergies.</summary>
    public List<string> Allergies { get; set; } = [];

    /// <summary>RETAINED for prod-soak drop. Use Profile.Intolerances.</summary>
    public List<string> Intolerances { get; set; } = [];

    /// <summary>RETAINED for prod-soak drop. Use Profile.AllergyOtherText.</summary>
    public string? AllergyOtherText { get; set; }

    /// <summary>RETAINED for prod-soak drop. Use Profile.IntoleranceOtherText.</summary>
    public string? IntoleranceOtherText { get; set; }

    /// <summary>RETAINED for prod-soak drop. Use Profile.MedicalConditions.</summary>
    public string? MedicalConditions { get; set; }

    /// <summary>
    /// When this profile was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When this profile was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }
}
