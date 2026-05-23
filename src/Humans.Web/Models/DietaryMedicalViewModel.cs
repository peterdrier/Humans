using System.ComponentModel.DataAnnotations;
using Humans.Domain.Entities;

namespace Humans.Web.Models;

public class DietaryMedicalViewModel
{
    [Required]
    public string DietaryPreference { get; set; } = string.Empty;

    public List<string> Allergies { get; set; } = [];

    [StringLength(500)]
    public string? AllergyOtherText { get; set; }

    public List<string> Intolerances { get; set; } = [];

    [StringLength(500)]
    public string? IntoleranceOtherText { get; set; }

    [StringLength(4000)]
    public string? MedicalConditions { get; set; }

    public static readonly string[] DietaryPreferences =
        ["Omnivore", "Vegetarian", "Vegan", "Pescatarian"];

    public static readonly string[] AllergyOptions =
        ["Peanut", "Tree nut", "Dairy", "Egg", "Shellfish", "Wheat/Gluten", "Soy", "Sesame", "Other"];

    public static readonly string[] IntoleranceOptions =
        ["Lactose", "Gluten", "Histamine", "FODMAP", "Other"];

    public static DietaryMedicalViewModel FromProfile(VolunteerEventProfile profile) => new()
    {
        DietaryPreference = profile.DietaryPreference ?? string.Empty,
        Allergies = [.. profile.Allergies],
        AllergyOtherText = profile.AllergyOtherText,
        Intolerances = [.. profile.Intolerances],
        IntoleranceOtherText = profile.IntoleranceOtherText,
        MedicalConditions = profile.MedicalConditions,
    };

    public void ApplyTo(VolunteerEventProfile profile)
    {
        profile.DietaryPreference = string.IsNullOrWhiteSpace(DietaryPreference) ? null : DietaryPreference;
        profile.Allergies = [.. Allergies.Where(IsKnownAllergy)];
        profile.AllergyOtherText = Allergies.Contains("Other") ? AllergyOtherText?.Trim() : null;
        profile.Intolerances = [.. Intolerances.Where(IsKnownIntolerance)];
        profile.IntoleranceOtherText = Intolerances.Contains("Other") ? IntoleranceOtherText?.Trim() : null;
        profile.MedicalConditions = string.IsNullOrWhiteSpace(MedicalConditions) ? null : MedicalConditions.Trim();
    }

    private static bool IsKnownAllergy(string v) => AllergyOptions.Contains(v);
    private static bool IsKnownIntolerance(string v) => IntoleranceOptions.Contains(v);
}
