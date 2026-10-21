
namespace Humans.Users.Contracts;

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
    bool AdminSuspension = false);

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
    string? ProfilePictureContentType);

public enum UserProfilePictureMutation
{
    None,
    Set,
    Remove,
}

/// <summary>Storage keys are null when the profile has no picture.</summary>
public sealed record UserProfileSaveResult(
    Guid ProfileId,
    string? PreviousProfilePictureKey,
    string? CurrentProfilePictureKey);

public sealed record UserProfileAnonymizeResult(
    bool Anonymized,
    Guid? ProfileId,
    string? PreviousProfilePictureKey);

public sealed record UserProfileLanguagesSaveResult(
    bool Saved,
    Guid? UserId);
