using NodaTime;

namespace Humans.Shifts.Domain;

/// <summary>Skills, quirks and languages for shift-matching. One per user.</summary>
internal sealed class VolunteerEventProfile
{
    public Guid Id { get; init; }

    /// <summary>Settable so the account-merge fold can re-FK it.</summary>
    public Guid UserId { get; set; }

    public List<string> Skills { get; set; } = [];

    public List<string> Quirks { get; set; } = [];

    public List<string> Languages { get; set; } = [];

    // Moved to Profile and retained only until the prod-verified drop
    // (memory/architecture/no-drops-until-prod-verified.md). Do NOT read or write these.
    public string? DietaryPreference { get; set; }

    public List<string> Allergies { get; set; } = [];

    public List<string> Intolerances { get; set; } = [];

    public string? AllergyOtherText { get; set; }

    public string? IntoleranceOtherText { get; set; }

    public string? MedicalConditions { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }
}
