using Humans.Users.Contracts;
using Humans.Base.Threading;
using Humans.Users.Helpers;

namespace Humans.Users.Services;

internal sealed class ProfileEditorService(
    IUserManagement userService,
    IContactFieldService contactFieldService,
    ILogger<ProfileEditorService> logger) : IProfileEditorService
{
    /// <summary>Refusal key for a CV payload with no entries and no "no prior experience" tick.</summary>
    internal const string BurnerCvRequiredErrorKey = "Users_Profile_BurnerCVRequired";
    internal const string InvalidLocationErrorKey = "Users_Profile_InvalidLocation";

    private const int MaxProfilePictureUploadBytes = 20 * 1024 * 1024;
    private static readonly HashSet<string> AllowedImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/heic",
        "image/heif",
        "image/avif"
    };
    private static readonly Dictionary<string, string> HeifExtensionToContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        [".heic"] = "image/heic",
        [".heif"] = "image/heif",
        [".avif"] = "image/avif"
    };

    // Serializes each user's save so picture metadata and file writes stay ordered.
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
        CancellationToken ct = default,
        IReadOnlyList<ContactFieldEditDto>? contactFields = null,
        IReadOnlyList<ProfileLanguageInfo>? languages = null,
        IReadOnlyCollection<Guid>? removedLegacyContactFieldIds = null)
    {
        var errorKey = ValidateSaveRequest(request);
        if (errorKey is not null)
            return Refuse(new(false, ErrorKey: errorKey));

        if (!ContactFieldService.IsValidPhone(request.EmergencyContactPhone))
            return Refuse(new(false, ErrorKey: "Validation_PhoneE164", ErrorField: nameof(request.EmergencyContactPhone),
                ErrorArgumentKey: "Users_Profile_EmergencyContactPhone"));
        if (ContactFieldService.ValidateContactFields(contactFields) is { } refusal)
            return Refuse(refusal);
        var picture = PreparePicture(request);
        if (picture.ErrorKey is { } pictureError)
            return Refuse(new(false, ErrorKey: pictureError, ErrorField: nameof(request.ProfilePictureData)));
        request = picture.Request!;

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
            if (contactFields is not null)
            {
                var contacts = await contactFieldService.SaveContactFieldsAsync(profileId, contactFields, ct,
                    removedLegacyContactFieldIds);
                if (!contacts.Succeeded) return contacts;
            }
            if (languages is not null)
                await userService.SaveProfileLanguagesAsync(profileId, languages, ct);
        }

        logger.LogInformation("User {UserId} updated their profile", userId);

        return new(true, profileId);

        ProfileEditResult Refuse(ProfileEditResult refusal)
        {
            logger.LogWarning("Profile save refused for {UserId}: {ErrorKey}", userId, refusal.ErrorKey);
            return refusal;
        }
    }

    // Enforced server-side regardless of caller.
    private static string? ValidateSaveRequest(ProfileSaveRequest request)
    {
        if (request.Latitude is { } latitude && latitude is not (>= -90d and <= 90d)
            || request.Longitude is { } longitude && longitude is not (>= -180d and <= 180d))
            return InvalidLocationErrorKey;

        // Entries or "no prior experience", checked only when a CV payload is present: name-only
        // saves leave history untouched and must not be blocked.
        if (request.VolunteerHistory is { Count: 0 } && !request.NoPriorBurnExperience)
        {
            return BurnerCvRequiredErrorKey;
        }
        return null;
    }

    private (ProfileSaveRequest? Request, string? ErrorKey) PreparePicture(ProfileSaveRequest request)
    {
        if (request.ProfilePictureData is not { } data)
            return (request, null);
        if (data.Length > MaxProfilePictureUploadBytes)
            return (null, "Users_Profile_PictureTooLarge");
        var contentType = request.ProfilePictureContentType;
        if (contentType is null || !AllowedImageContentTypes.Contains(contentType))
        {
            var extension = Path.GetExtension(request.ProfilePictureFileName);
            if (extension is not null && HeifExtensionToContentType.TryGetValue(extension, out var mapped))
                contentType = mapped;
        }
        if (contentType is null || !AllowedImageContentTypes.Contains(contentType))
            return (null, "Users_Profile_PictureInvalidFormat");
        var resized = ProfilePictureProcessor.ResizeProfilePicture(data, logger);
        return resized is { } image
            ? (request with { ProfilePictureData = image.Data, ProfilePictureContentType = image.ContentType }, null)
            : (null, "Users_Profile_PictureInvalidFormat");
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
