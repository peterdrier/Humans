using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Microsoft.Extensions.Logging;

namespace Humans.Application.Services.Profiles;

public sealed class ProfileEditorService(
    IUserService userService,
    IFileStorage fileStorage,
    ILogger<ProfileEditorService> logger) : IProfileEditorService
{
    // Serialize full save orchestration so DB picture metadata and file writes stay ordered per user.
    private static readonly SemaphoreSlim[] UserLocks = CreateUserLocks(32);

    private static SemaphoreSlim[] CreateUserLocks(int count)
    {
        var locks = new SemaphoreSlim[count];
        for (var i = 0; i < count; i++) locks[i] = new SemaphoreSlim(1, 1);
        return locks;
    }

    private static SemaphoreSlim LockFor(Guid userId) =>
        UserLocks[(uint)userId.GetHashCode() % (uint)UserLocks.Length];

    public async Task<Guid> SaveProfileAsync(
        Guid userId,
        string displayName,
        ProfileSaveRequest request,
        CancellationToken ct = default)
    {
        var gate = LockFor(userId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var storageResult = await userService.SaveProfileAsync(
                userId,
                ToUserProfileSaveCommand(displayName, request),
                ct);

            await ApplyProfilePictureFileMutationAsync(storageResult, request, ct);

            logger.LogInformation("User {UserId} updated their profile", userId);

            return storageResult.ProfileId;
        }
        finally
        {
            gate.Release();
        }
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

    public Task SaveDietaryMedicalAsync(
        Guid userId,
        UserProfileDietaryMedicalCommand command,
        CancellationToken ct = default) =>
        userService.SaveDietaryMedicalAsync(userId, command, ct);

    private async Task ApplyProfilePictureFileMutationAsync(
        UserProfileSaveResult storageResult,
        ProfileSaveRequest request,
        CancellationToken ct)
    {
        if (request.RemoveProfilePicture)
        {
            if (storageResult.PreviousProfilePictureContentType is not null)
            {
                try
                {
                    await fileStorage.DeleteAsync(
                        ProfilePictureStorageKeys.ProfilePictureKey(
                            storageResult.ProfileId,
                            storageResult.PreviousProfilePictureContentType),
                        ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "Failed to delete profile picture from filesystem for {ProfileId}; content-type column has been cleared so the file will not be served",
                        storageResult.ProfileId);
                }
            }

            return;
        }

        if (request.ProfilePictureData is null || request.ProfilePictureContentType is null)
            return;

        try
        {
            if (storageResult.PreviousProfilePictureContentType is not null &&
                !string.Equals(storageResult.PreviousProfilePictureContentType, request.ProfilePictureContentType, StringComparison.Ordinal))
            {
                await fileStorage.DeleteAsync(
                    ProfilePictureStorageKeys.ProfilePictureKey(
                        storageResult.ProfileId,
                        storageResult.PreviousProfilePictureContentType),
                    ct);
            }

            await fileStorage.SaveAsync(
                ProfilePictureStorageKeys.ProfilePictureKey(
                    storageResult.ProfileId,
                    request.ProfilePictureContentType),
                request.ProfilePictureData,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to write profile picture to filesystem for {ProfileId}; content-type column is set but the file is missing - picture will not render",
                storageResult.ProfileId);
        }
    }
}
