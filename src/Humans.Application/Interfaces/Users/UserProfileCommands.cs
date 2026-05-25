using Humans.Domain.Enums;

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
    bool? Suspended = null);

public enum UserProfileOnboardingMutation
{
    RecordConsentCheck,
    RejectSignup,
    ApproveVolunteer,
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
    string? AllergyOtherText = null);

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
    string? MedicalConditions);

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
