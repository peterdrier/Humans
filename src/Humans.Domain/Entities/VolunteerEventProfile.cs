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

    /// <summary>
    /// When this profile was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When this profile was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }
}
