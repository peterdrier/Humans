namespace Humans.Users.Contracts;

/// <summary>
/// Focused write for the full dietary + medical set (the DietaryMedical page).
/// Updates only these six Profile columns; leaves all other profile fields untouched.
/// MedicalConditions is GDPR Art. 9 — callers must already have verified the
/// editor is the owner (or an authorized admin).
/// </summary>
/// <remarks>
/// Lives on the leaf rather than beside the rest of <c>UserProfileCommands</c> because
/// <see cref="IProfileEditorService.SaveDietaryMedicalAsync"/> names it and Onboarding
/// injects that interface. The sibling commands are <c>IUserService</c>'s surface and stay
/// with the section (nobodies-collective/Humans#866, lane 2 PR A).
/// </remarks>
public sealed record UserProfileDietaryMedicalCommand(
    string? DietaryPreference,
    List<string> Allergies,
    string? AllergyOtherText,
    List<string> Intolerances,
    string? IntoleranceOtherText,
    string? MedicalConditions);
