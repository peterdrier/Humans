using Humans.Users.Data.Repositories;
using Humans.Users.Contracts;
using Humans.Base.Interfaces;

namespace Humans.Users.Services;

internal sealed class ProfileService(IUserRepository userRepository,
    IUserServiceInternal userService,
    IFileStorage fileStorage) : IProfilePictureService
{
    public async Task<(byte[] Data, string ContentType)?> GetProfilePictureAsync(
        Guid profileId, CancellationToken ct = default)
    {
        // GDPR gate: content-type column is the source of truth — don't serve from disk if cleared.
        var dbContentType = await userRepository.GetProfilePictureContentTypeAsync(profileId, ct);
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
        var rows = await userRepository.GetCustomPictureRowsAsync(ct);
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
