using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Users;

/// <summary>
/// Consolidated storage mutation for profile-side onboarding state carried by
/// <see cref="UserInfo"/>. Callers own workflow policy and audit text;
/// <c>IUserService</c> owns the profile row write.
/// </summary>
public sealed record UserProfileOnboardingCommand(
    UserProfileOnboardingMutation Mutation,
    Guid? ActorUserId = null,
    ConsentCheckStatus? ConsentCheckStatus = null,
    string? Notes = null,
    string? RejectionReason = null,
    bool? Suspended = null,
    bool AdminSuspension = false)
{
    public void Validate(string argumentName = "command")
    {
        switch (Mutation)
        {
            case UserProfileOnboardingMutation.RecordConsentCheck
                when ConsentCheckStatus is not Humans.Domain.Enums.ConsentCheckStatus.Cleared
                    and not Humans.Domain.Enums.ConsentCheckStatus.Flagged:
                throw new ArgumentException(
                    "RecordConsentCheck only accepts Cleared or Flagged; use SetConsentCheckPending for the system-driven Pending transition.",
                    argumentName);

            case UserProfileOnboardingMutation.SetSuspension when Suspended is null:
                throw new ArgumentException("SetSuspension requires Suspended.", argumentName);
        }
    }
}

public enum UserProfileOnboardingMutation
{
    RecordConsentCheck,
    RejectSignup,
    SetSuspension,
    SetConsentCheckPending,
}

public sealed record UserProfileSaveCommand(
    string DisplayName,
    string BurnerName,
    string FirstName,
    string LastName,
    string? City,
    string? CountryCode,
    double? Latitude,
    double? Longitude,
    string? PlaceId,
    string? Bio,
    string? Pronouns,
    string? ContributionInterests,
    string? BoardNotes,
    int? BirthdayMonth,
    int? BirthdayDay,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    string? EmergencyContactRelationship,
    bool NoPriorBurnExperience,
    UserProfilePictureMutation PictureMutation,
    string? ProfilePictureContentType,
    // Edit-page-owned dietary fields (meal pref + allergies). Intolerances +
    // medical are written separately via UserProfileDietaryMedicalCommand.
    string? DietaryPreference = null,
    List<string>? Allergies = null,
    string? AllergyOtherText = null)
{
    public string? BioForSave() => Bio?.TrimEnd();

    public string? ContributionInterestsForSave() => ContributionInterests?.TrimEnd();

    public string? BoardNotesForSave() => BoardNotes?.TrimEnd();

    public bool HasDietaryPatch() =>
        DietaryPreference is not null || Allergies is not null || AllergyOtherText is not null;

    public string? DietaryPreferenceForSave() =>
        string.IsNullOrWhiteSpace(DietaryPreference) ? null : DietaryPreference;

    public IReadOnlyList<string> AllergiesForSave() => Allergies ?? [];

    public LocalDate? BirthDateOrNull()
    {
        if (BirthdayMonth is not (>= 1 and <= 12) || BirthdayDay is not (>= 1 and <= 31))
            return null;

        try
        {
            // Year 4 lets Feb 29 validate without storing a real birth year.
            return new LocalDate(4, BirthdayMonth.Value, BirthdayDay.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public string? ProfilePictureContentTypeForSave(string? currentContentType) =>
        PictureMutation switch
        {
            UserProfilePictureMutation.Remove => null,
            UserProfilePictureMutation.Set => ProfilePictureContentType,
            _ => currentContentType,
        };
}

/// <summary>
/// Focused write for the full dietary + medical set (the DietaryMedical page).
/// Updates only these six Profile columns; leaves all other profile fields untouched.
/// MedicalConditions is GDPR Art. 9 — callers must already have verified the
/// editor is the owner (or an authorized admin).
/// </summary>
public sealed record UserProfileDietaryMedicalCommand(
    string? DietaryPreference,
    List<string> Allergies,
    string? AllergyOtherText,
    List<string> Intolerances,
    string? IntoleranceOtherText,
    string? MedicalConditions)
{
    public void ApplyTo(Profile profile, Instant updatedAt)
    {
        profile.DietaryPreference = DietaryPreference;
        profile.Allergies = Allergies;
        profile.AllergyOtherText = AllergyOtherText;
        profile.Intolerances = Intolerances;
        profile.IntoleranceOtherText = IntoleranceOtherText;
        profile.MedicalConditions = MedicalConditions;
        profile.UpdatedAt = updatedAt;
    }
}

public enum UserProfilePictureMutation
{
    None,
    Set,
    Remove,
}

public sealed record UserProfileSaveResult(
    Guid ProfileId,
    string? PreviousProfilePictureContentType,
    string? CurrentProfilePictureContentType);

public sealed record UserProfilePictureContentTypeResult(
    bool Saved,
    Guid? ProfileId,
    string? PreviousProfilePictureContentType,
    string? CurrentProfilePictureContentType);

public sealed record UserProfileAnonymizeResult(
    bool Anonymized,
    Guid? ProfileId,
    string? PreviousProfilePictureContentType);

public sealed record UserProfileLanguagesSaveResult(
    bool Saved,
    Guid? UserId);
