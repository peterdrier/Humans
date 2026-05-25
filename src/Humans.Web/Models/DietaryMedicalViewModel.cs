using System.ComponentModel.DataAnnotations;
using Humans.Application;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;

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

    // Carryover from the redirect-then-replay flow (see dietary-prompt-tightening design).
    // Not bound to VolunteerEventProfile — pure round-trip routing data so the POST handler
    // can replay the original ShiftsController.SignUp / SignUpRange after a successful save.
    public string? ReturnAction { get; set; }
    public Guid? ShiftId { get; set; }
    public Guid? RotaId { get; set; }
    public int? StartDayOffset { get; set; }
    public int? EndDayOffset { get; set; }

    /// <summary>
    /// Sentinel value used in <see cref="AllergyOptions"/> and <see cref="IntoleranceOptions"/>
    /// to indicate a free-text follow-up. Forwards to the canonical Domain constant so the
    /// VM, controller validation, view, and the Cantina roster service stay in sync.
    /// </summary>
    public const string OtherOption = DietaryOptions.OtherOption;

    /// <summary>Forwarder to <see cref="DietaryOptions.DietaryPreferences"/>.</summary>
    public static readonly IReadOnlyList<string> DietaryPreferences = DietaryOptions.DietaryPreferences;

    /// <summary>Forwarder to <see cref="DietaryOptions.AllergyOptions"/>.</summary>
    public static readonly IReadOnlyList<string> AllergyOptions = DietaryOptions.AllergyOptions;

    /// <summary>Forwarder to <see cref="DietaryOptions.IntoleranceOptions"/>.</summary>
    public static readonly IReadOnlyList<string> IntoleranceOptions = DietaryOptions.IntoleranceOptions;

    public static DietaryMedicalViewModel FromProfile(ProfileInfo profile) => new()
    {
        DietaryPreference = profile.DietaryPreference ?? string.Empty,
        Allergies = [.. profile.Allergies],
        AllergyOtherText = profile.AllergyOtherText,
        Intolerances = [.. profile.Intolerances],
        IntoleranceOtherText = profile.IntoleranceOtherText,
        MedicalConditions = profile.MedicalConditions,
    };

    /// <summary>
    /// Normalizes the posted form into the storage command: unknown chips dropped,
    /// "Other" free-text kept only when "Other" is selected, blanks coalesced to null.
    /// </summary>
    public UserProfileDietaryMedicalCommand ToCommand() => new(
        DietaryPreference: string.IsNullOrWhiteSpace(DietaryPreference) ? null : DietaryPreference,
        Allergies: [.. Allergies.Where(IsKnownAllergy)],
        AllergyOtherText: Allergies.Contains(OtherOption) ? AllergyOtherText?.Trim() : null,
        Intolerances: [.. Intolerances.Where(IsKnownIntolerance)],
        IntoleranceOtherText: Intolerances.Contains(OtherOption) ? IntoleranceOtherText?.Trim() : null,
        MedicalConditions: string.IsNullOrWhiteSpace(MedicalConditions) ? null : MedicalConditions.Trim());

    private static bool IsKnownAllergy(string v) => AllergyOptions.Contains(v, StringComparer.Ordinal);
    private static bool IsKnownIntolerance(string v) => IntoleranceOptions.Contains(v, StringComparer.Ordinal);
}
