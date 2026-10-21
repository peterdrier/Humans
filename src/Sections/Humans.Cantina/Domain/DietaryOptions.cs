namespace Humans.Cantina.Domain;

/// <summary>
/// Canonical option lists for a member's dietary profile. Single source of truth for the
/// chips/checkboxes on the <c>/Cantina/Dietary</c> form, the filter the save applies before
/// persisting, and the labels the roster roll-up counts.
/// </summary>
internal static class DietaryOptions
{
    /// <summary>
    /// Sentinel value used in <see cref="AllergyOptions"/> and
    /// <see cref="IntoleranceOptions"/> to flag a free-text follow-up.
    /// When selected, the corresponding "*OtherText" field on
    /// <see cref="DietaryProfile"/> carries the user-supplied detail.
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
        ["Lactose", "Gluten", "Histamine", OtherOption];
}
