using NodaTime;

namespace Humans.Cantina.Domain;

/// <summary>
/// Replaces the five food columns on <c>profiles</c>, which Users keeps unwritten until the
/// <c>cantina.profile-dietary</c> data migration is done in production. Medical conditions stay in Users.
/// </summary>
internal sealed class DietaryProfile
{
    /// <summary>Bare id, no FK to <c>users</c>.</summary>
    public Guid UserId { get; init; }

    /// <summary>A row with a null preference still records that the member saved the form, so the read never falls
    /// back to the legacy profile values.</summary>
    public string? DietaryPreference { get; set; }

    public List<string> Allergies { get; set; } = [];

    public string? AllergyOtherText { get; set; }

    public List<string> Intolerances { get; set; } = [];

    public string? IntoleranceOtherText { get; set; }

    /// <summary>"I don't want to answer": every food field is empty and the prompts stop. Saving answers clears it.
    /// Null means unanswered.</summary>
    public bool? DeclinedToAnswer { get; set; }

    public Instant UpdatedAt { get; set; }
}
