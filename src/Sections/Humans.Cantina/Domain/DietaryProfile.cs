using NodaTime;

namespace Humans.Cantina.Domain;

/// <summary>A member's food answers. Medical conditions stay in Users.</summary>
internal sealed class DietaryProfile
{
    /// <summary>Bare id, no FK to <c>users</c>.</summary>
    public Guid UserId { get; init; }

    /// <summary>A null preference is unanswered, even when allergy or intolerance answers are saved.</summary>
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
