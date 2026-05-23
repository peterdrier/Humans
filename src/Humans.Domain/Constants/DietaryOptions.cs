namespace Humans.Domain.Constants;

/// <summary>
/// Canonical option lists for the dietary/medical profile section.
/// Single source of truth for the chips/checkboxes rendered on the
/// volunteer's DietaryMedical form and the labels used by the Cantina
/// daily-roster roll-up. Keeping the lists here (Domain) lets the
/// Application layer reference them without taking a dependency on
/// the Web layer's view models.
/// </summary>
public static class DietaryOptions
{
    /// <summary>
    /// Sentinel value used in <see cref="AllergyOptions"/> and
    /// <see cref="IntoleranceOptions"/> to flag a free-text follow-up.
    /// When selected, the corresponding "*OtherText" field on
    /// <see cref="Humans.Domain.Entities.VolunteerEventProfile"/>
    /// carries the user-supplied detail.
    /// </summary>
    public const string OtherOption = "Other";

    /// <summary>The four dietary preference categories.</summary>
    public static readonly IReadOnlyList<string> DietaryPreferences =
        ["Omnivore", "Vegetarian", "Vegan", "Pescatarian"];

    /// <summary>
    /// Canonical allergy chips. <see cref="OtherOption"/> is included
    /// (and conventionally rendered last) for the free-text follow-up.
    /// </summary>
    public static readonly IReadOnlyList<string> AllergyOptions =
        ["Peanut", "Tree nut", "Dairy", "Egg", "Shellfish", "Wheat/Gluten", "Soy", "Sesame", OtherOption];

    /// <summary>
    /// Canonical intolerance chips. <see cref="OtherOption"/> is included
    /// (and conventionally rendered last) for the free-text follow-up.
    /// </summary>
    public static readonly IReadOnlyList<string> IntoleranceOptions =
        ["Lactose", "Gluten", "Histamine", "FODMAP", OtherOption];
}
