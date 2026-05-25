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
    string? ProfilePictureContentType);

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
