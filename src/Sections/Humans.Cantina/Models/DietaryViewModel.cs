using System.ComponentModel.DataAnnotations;
using Humans.Cantina.Contracts;
using Humans.Cantina.Domain;

namespace Humans.Cantina.Models;

internal sealed class DietaryViewModel
{
    [Required(ErrorMessage = "Validation_Required")]
    public string DietaryPreference { get; set; } = string.Empty;

    public List<string> Allergies { get; set; } = [];

    [StringLength(500, ErrorMessage = "Validation_MaxLength")]
    public string? AllergyOtherText { get; set; }

    public List<string> Intolerances { get; set; } = [];

    [StringLength(500, ErrorMessage = "Validation_MaxLength")]
    public string? IntoleranceOtherText { get; set; }

    /// <summary>Where to land after the POST: <c>"shifts"</c> (the /Shifts banner) or Home.</summary>
    public string? ReturnAction { get; set; }

    /// <summary>Posted by the "I don't want to answer" buttons; the answers are then ignored.</summary>
    public bool DeclineToAnswer { get; set; }

    /// <summary>The member's stored choice, shown as a note on the form.</summary>
    public bool DeclinedToAnswer { get; set; }

    public static DietaryViewModel FromProfile(DietaryProfileInfo? profile) => profile is null ? new() : new()
    {
        DeclinedToAnswer = profile.DeclinedToAnswer,
        DietaryPreference = profile.DietaryPreference ?? string.Empty,
        Allergies = [.. profile.Allergies],
        AllergyOtherText = profile.AllergyOtherText,
        Intolerances = [.. profile.Intolerances],
        IntoleranceOtherText = profile.IntoleranceOtherText,
    };

    /// <summary>The posted answers as given; the service normalizes them.</summary>
    public DietaryProfileInfo ToAnswers(Guid userId) => new(
        userId, DietaryPreference, Allergies, AllergyOtherText, Intolerances, IntoleranceOtherText);

    public bool OtherAllergyWithoutText =>
        Allergies.Contains(DietaryOptions.OtherOption) && string.IsNullOrWhiteSpace(AllergyOtherText);

    public bool OtherIntoleranceWithoutText =>
        Intolerances.Contains(DietaryOptions.OtherOption) && string.IsNullOrWhiteSpace(IntoleranceOtherText);
}
