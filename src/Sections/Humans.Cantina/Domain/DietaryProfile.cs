using NodaTime;

namespace Humans.Cantina.Domain;

/// <summary>
/// What a member can and cannot eat, one row per member (<c>cantina_dietary_profiles</c>).
/// Replaces the five food columns on <c>profiles</c>, which Users keeps unwritten until the
/// <c>cantina.profile-dietary</c> data migration is done in production. Medical conditions are
/// not here: they are GDPR Art. 9 data and stay in Users.
/// </summary>
internal sealed class DietaryProfile
{
    /// <summary>The member. Bare id, no FK to <c>users</c>
    /// (memory/architecture/no-cross-section-ef-joins.md).</summary>
    public Guid UserId { get; init; }

    /// <summary>One of <see cref="DietaryOptions.DietaryPreferences"/>; null when unanswered.
    /// A row with a null preference still records that the member saved the form, so the read
    /// never falls back to the legacy profile values.</summary>
    public string? DietaryPreference { get; set; }

    public List<string> Allergies { get; set; } = [];

    /// <summary>Free text, present only when <see cref="DietaryOptions.OtherOption"/> is an allergy.</summary>
    public string? AllergyOtherText { get; set; }

    public List<string> Intolerances { get; set; } = [];

    /// <summary>Free text, present only when <see cref="DietaryOptions.OtherOption"/> is an intolerance.</summary>
    public string? IntoleranceOtherText { get; set; }

    /// <summary>The member chose "I don't want to answer": every food field is empty and the
    /// dietary prompts stop. Saving answers clears it (Docs/features/dietary-nudge.md).</summary>
    public bool DeclinedToAnswer { get; set; }

    public Instant UpdatedAt { get; set; }
}
