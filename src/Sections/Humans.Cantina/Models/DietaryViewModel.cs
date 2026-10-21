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

    // Carryover from the dietary-gate redirect (ShiftsController.ToggleDay): pure round-trip
    // routing data so the POST can replay the original signup after a successful save
    // (Docs/features/dietary-nudge.md, US-35.6).
    public string? ReturnAction { get; set; }
    public Guid? ShiftId { get; set; }
    public Guid? RotaId { get; set; }
    public int? StartDayOffset { get; set; }
    public int? EndDayOffset { get; set; }

    public static DietaryViewModel FromProfile(DietaryProfileInfo? profile) => profile is null ? new() : new()
    {
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
