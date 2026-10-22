namespace Humans.Cantina.Domain;

/// <summary>The one source for the form's chips, the save's filter and the roster roll-up labels.</summary>
internal static class DietaryOptions
{
    /// <summary>Flags a free-text follow-up, carried in the matching "*OtherText" field.</summary>
    public const string OtherOption = "Other";

    public static readonly IReadOnlyList<string> DietaryPreferences =
        ["Omnivore", "Vegetarian", "Vegan", "Pescatarian"];

    public static readonly IReadOnlyList<string> AllergyOptions =
        ["Peanut", "Tree nut", "Dairy", "Egg", "Shellfish", "Wheat/Gluten", "Soy", "Sesame", OtherOption];

    public static readonly IReadOnlyList<string> IntoleranceOptions =
        ["Lactose", "Gluten", "Histamine", OtherOption];
}
