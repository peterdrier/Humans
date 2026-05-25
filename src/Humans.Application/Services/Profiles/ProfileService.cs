using Microsoft.Extensions.Logging;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Services.Profiles;

public sealed class ProfileService(IProfileRepository profileRepository,
    IUserService userService,
    IFileStorage fileStorage,
    ILogger<ProfileService> logger) : IProfilePictureService
{
    public async Task SetProfilePictureAsync(
        Guid userId, byte[] pictureData, string contentType, CancellationToken ct = default)
    {
        if (pictureData.Length == 0)
        {
            throw new ArgumentException("Picture data must not be empty", nameof(pictureData));
        }
        if (string.IsNullOrWhiteSpace(contentType))
        {
            throw new ArgumentException("Content type must not be empty", nameof(contentType));
        }

        var storageResult = await userService.SetProfilePictureContentTypeAsync(userId, contentType, ct);
        if (!storageResult.Saved || storageResult.ProfileId is null)
        {
            logger.LogWarning(
                "Cannot set profile picture for user {UserId} - no profile exists", userId);
            return;
        }

        try
        {
            // Remove old file if content-type/extension changed.
            if (storageResult.PreviousProfilePictureContentType is not null &&
                !string.Equals(storageResult.PreviousProfilePictureContentType, contentType, StringComparison.Ordinal))
            {
                await fileStorage.DeleteAsync(
                    ProfilePictureStorageKeys.ProfilePictureKey(
                        storageResult.ProfileId.Value,
                        storageResult.PreviousProfilePictureContentType),
                    ct);
            }
            await fileStorage.SaveAsync(
                ProfilePictureStorageKeys.ProfilePictureKey(storageResult.ProfileId.Value, contentType),
                pictureData,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to write profile picture to filesystem for {ProfileId}; content-type column is set but the file is missing - picture will not render",
                storageResult.ProfileId.Value);
        }

    }

    public async Task<(byte[] Data, string ContentType)?> GetProfilePictureAsync(
        Guid profileId, CancellationToken ct = default)
    {
        // GDPR gate: content-type column is the source of truth — don't serve from disk if cleared.
        var dbContentType = await profileRepository.GetProfilePictureContentTypeAsync(profileId, ct);
        if (string.IsNullOrEmpty(dbContentType))
        {
            return null;
        }

        var key = ProfilePictureStorageKeys.ProfilePictureKey(profileId, dbContentType);
        var fsBytes = await fileStorage.TryReadAsync(key, ct);
        return fsBytes is not null ? (fsBytes, dbContentType) : null;
    }

    public async Task<ProfilePictureMigrationSnapshot> GetProfilePictureMigrationSnapshotAsync(
        CancellationToken ct = default)
    {
        var rows = await profileRepository.GetCustomPictureRowsAsync(ct);
        if (rows.Count == 0)
        {
            return new ProfilePictureMigrationSnapshot(0, 0, []);
        }

        var users = await userService.GetUserInfosAsync(rows.Select(r => r.UserId).ToList(), ct);

        var onFs = 0;
        var dbOnly = new List<ProfilePictureMigrationRow>();
        foreach (var (profileId, userId, burnerName, contentType, updatedAt) in rows)
        {
            var key = ProfilePictureStorageKeys.ProfilePictureKey(profileId, contentType);
            var bytes = await fileStorage.TryReadAsync(key, ct);
            if (bytes is not null)
            {
                onFs++;
            }
            else
            {
                var displayName = !string.IsNullOrWhiteSpace(burnerName)
                    ? burnerName
                    : (users.TryGetValue(userId, out var u) ? u.BurnerName : string.Empty);
                dbOnly.Add(new ProfilePictureMigrationRow(profileId, userId, displayName, contentType, updatedAt));
            }
        }

        return new ProfilePictureMigrationSnapshot(rows.Count, onFs, dbOnly);
    }
}
