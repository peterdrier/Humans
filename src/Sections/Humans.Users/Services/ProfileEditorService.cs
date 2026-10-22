using Humans.Users.Contracts;
using Humans.Base.Threading;

namespace Humans.Users.Services;

internal sealed class ProfileEditorService(
    IUserManagement userService,
    ILogger<ProfileEditorService> logger) : IProfileEditorService
{
    /// <summary>Refusal key for a CV payload with no entries and no "no prior experience" tick.</summary>
    internal const string BurnerCvRequiredErrorKey = "Users_Profile_BurnerCVRequired";

    // Serialize full save orchestration so DB picture metadata and file writes stay ordered per user.
    private static readonly TrackedLock[] UserLocks = CreateUserLocks(32);

    private static TrackedLock[] CreateUserLocks(int count)
    {
        var locks = new TrackedLock[count];
        for (var i = 0; i < count; i++) locks[i] = new TrackedLock($"ProfileEditor.User[{i}]");
        return locks;
    }

    private static TrackedLock LockFor(Guid userId) =>
        UserLocks[(uint)userId.GetHashCode() % (uint)UserLocks.Length];

    public async Task<ProfileEditResult> SaveProfileAsync(
        Guid userId,
        ProfileSaveRequest request,
        CancellationToken ct = default)
    {
        var errorKey = ValidateSaveRequest(request);
        if (errorKey is not null)
        {
            logger.LogWarning("Profile save refused for {UserId}: {ErrorKey}", userId, errorKey);
            return new(false, ErrorKey: errorKey);
        }

        Guid profileId;
        using (await LockFor(userId).AcquireAsync(logger, ct))
        {
            var storageResult = await userService.SaveProfileAsync(
                userId,
                ToUserProfileSaveCommand(request),
                request.ProfilePictureData,
                ct,
                request.VolunteerHistory);

            profileId = storageResult.ProfileId;
        }

        logger.LogInformation("User {UserId} updated their profile", userId);

        return new(true, profileId);
    }

    // Cross-field invariants enforced server-side regardless of caller (P1/P2).
    private static string? ValidateSaveRequest(ProfileSaveRequest request)
    {
        // Burner CV completeness: entries OR "no prior experience". Only checked
        // when the request carries a CV payload — name-only saves leave history
        // untouched and must not be blocked.
        if (request.VolunteerHistory is { Count: 0 } && !request.NoPriorBurnExperience)
        {
            return BurnerCvRequiredErrorKey;
        }
        return null;
    }

    private static UserProfileSaveCommand ToUserProfileSaveCommand(
        ProfileSaveRequest request)
    {
        var pictureMutation = request.RemoveProfilePicture
            ? UserProfilePictureMutation.Remove
            : request.ProfilePictureData is not null && request.ProfilePictureContentType is not null
                ? UserProfilePictureMutation.Set
                : UserProfilePictureMutation.None;

        return new UserProfileSaveCommand(
            BurnerName: request.BurnerName,
            FirstName: request.FirstName,
            LastName: request.LastName,
            City: request.City,
            CountryCode: request.CountryCode,
            Latitude: request.Latitude,
            Longitude: request.Longitude,
            PlaceId: request.PlaceId,
            Bio: request.Bio,
            Pronouns: request.Pronouns,
            ContributionInterests: request.ContributionInterests,
            BoardNotes: request.BoardNotes,
            BirthdayMonth: request.BirthdayMonth,
            BirthdayDay: request.BirthdayDay,
            EmergencyContactName: request.EmergencyContactName,
            EmergencyContactPhone: request.EmergencyContactPhone,
            EmergencyContactRelationship: request.EmergencyContactRelationship,
            NoPriorBurnExperience: request.NoPriorBurnExperience,
            PictureMutation: pictureMutation,
            ProfilePictureContentType: request.ProfilePictureContentType);
    }
}
