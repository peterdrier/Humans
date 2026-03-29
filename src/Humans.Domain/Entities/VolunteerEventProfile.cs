using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// User-scoped volunteer shift profile with skills, dietary info, and medical data.
/// One-to-one with User.
/// </summary>
public class VolunteerEventProfile
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// FK to the volunteer (1:1).
    /// </summary>
    public Guid UserId { get; init; }

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
    /// Dietary preference (e.g., "Vegan", "Vegetarian", "Omnivore", "Pescatarian").
    /// </summary>
    public string? DietaryPreference { get; set; }

    /// <summary>
    /// Food allergies.
    /// </summary>
    public List<string> Allergies { get; set; } = [];

    /// <summary>
    /// Food intolerances.
    /// </summary>
    public List<string> Intolerances { get; set; } = [];

    /// <summary>
    /// Free text specifying "Other" allergy when "Other" is selected.
    /// </summary>
    public string? AllergyOtherText { get; set; }

    /// <summary>
    /// Free text specifying "Other" intolerance when "Other" is selected.
    /// </summary>
    public string? IntoleranceOtherText { get; set; }

    /// <summary>
    /// Medical conditions (restricted visibility — owner/NoInfoAdmin/Admin only).
    /// </summary>
    public string? MedicalConditions { get; set; }

    /// <summary>
    /// When this profile was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When this profile was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }

    // Navigation
    public User User { get; set; } = null!;
}
