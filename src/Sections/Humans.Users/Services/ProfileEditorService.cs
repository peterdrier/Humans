using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Humans.Base.Threading;

namespace Humans.Users.Services;

internal sealed class ProfileEditorService(
    IUserServiceInternal userService,
    IFileStorage fileStorage,
    ILogger<ProfileEditorService> logger) : IProfileEditorService
{
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
        string displayName,
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
                ToUserProfileSaveCommand(displayName, request),
                request.ProfilePictureData,
                ct);

            await CleanupPreviousProfilePictureAsync(storageResult);
            profileId = storageResult.ProfileId;
        }

        if (request.VolunteerHistory is not null)
            await userService.SaveProfileVolunteerHistoryAsync(userId, request.VolunteerHistory.ToList(), ct);

        logger.LogInformation("User {UserId} updated their profile", userId);

        return new(true, profileId);
    }

    // Cross-field invariants enforced server-side regardless of caller (P1/P2).
    private static string? ValidateSaveRequest(ProfileSaveRequest request)
    {
        if (request.Allergies is not null
            && request.Allergies.Contains(DietaryOptions.OtherOption, StringComparer.Ordinal)
            && string.IsNullOrWhiteSpace(request.AllergyOtherText))
        {
            return "Profile_DietaryMedical_AllergyOther_Required";
        }

        // Burner CV completeness: entries OR "no prior experience". Only checked
        // when the request carries a CV payload — name-only saves leave history
        // untouched and must not be blocked.
        if (request.VolunteerHistory is { Count: 0 } && !request.NoPriorBurnExperience)
        {
            return "Profile_BurnerCVRequired";
        }
        return null;
    }

    private static UserProfileSaveCommand ToUserProfileSaveCommand(
        string displayName,
        ProfileSaveRequest request)
    {
        var pictureMutation = request.RemoveProfilePicture
            ? UserProfilePictureMutation.Remove
            : request.ProfilePictureData is not null && request.ProfilePictureContentType is not null
                ? UserProfilePictureMutation.Set
                : UserProfilePictureMutation.None;

        return new UserProfileSaveCommand(
            DisplayName: displayName,
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
            ProfilePictureContentType: request.ProfilePictureContentType,
            DietaryPreference: request.DietaryPreference,
            Allergies: request.Allergies,
            AllergyOtherText: request.AllergyOtherText);
    }

    public async Task<ProfileEditResult> SaveDietaryMedicalAsync(
        Guid userId,
        UserProfileDietaryMedicalCommand command,
        CancellationToken ct = default)
    {
        // "Other" requires accompanying free text, as on the full profile form.
        string? errorKey = null;
        if (command.Allergies.Contains(DietaryOptions.OtherOption, StringComparer.Ordinal)
            && string.IsNullOrWhiteSpace(command.AllergyOtherText))
        {
            errorKey = "Profile_DietaryMedical_AllergyOther_Required";
        }
        else if (command.Intolerances.Contains(DietaryOptions.OtherOption, StringComparer.Ordinal)
                 && string.IsNullOrWhiteSpace(command.IntoleranceOtherText))
        {
            errorKey = "Profile_DietaryMedical_IntoleranceOther_Required";
        }
        if (errorKey is not null)
        {
            logger.LogWarning("Dietary/medical save refused for {UserId}: {ErrorKey}", userId, errorKey);
            return new(false, ErrorKey: errorKey);
        }

        await userService.SaveDietaryMedicalAsync(userId, command, ct);
        return new(true);
    }

    private async Task CleanupPreviousProfilePictureAsync(UserProfileSaveResult storageResult)
    {
        if (storageResult.PreviousProfilePictureContentType is null
            || string.Equals(storageResult.PreviousProfilePictureContentType,
                storageResult.CurrentProfilePictureContentType, StringComparison.Ordinal))
            return;

        try
        {
            await fileStorage.DeleteAsync(
                ProfilePictureStorageKeys.ProfilePictureKey(
                    storageResult.ProfileId, storageResult.PreviousProfilePictureContentType),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to delete superseded profile picture for {ProfileId}; current picture metadata is already committed",
                storageResult.ProfileId);
        }
    }
}
