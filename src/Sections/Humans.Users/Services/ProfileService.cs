using Humans.Users.Data.Repositories;
using Humans.Users.Contracts;
using Humans.Base.Interfaces;

namespace Humans.Users.Services;

internal sealed class ProfileService(IUserRepository userRepository,
    IFileStorage fileStorage) : IProfilePictureService
{
    public async Task<(byte[] Data, string ContentType)?> GetProfilePictureAsync(
        Guid profileId, CancellationToken ct = default)
    {
        // GDPR gate: content-type column is the source of truth — don't serve from disk if cleared.
        var location = await userRepository.GetProfilePictureLocationAsync(profileId, ct);
        if (location is not { } picture || string.IsNullOrEmpty(picture.ContentType))
        {
            return null;
        }

        var key = ProfilePictureKeys.Resolve(profileId, picture.ContentType, picture.StorageKey)!;
        var fsBytes = await fileStorage.TryReadAsync(key, ct);
        return fsBytes is not null ? (fsBytes, picture.ContentType) : null;
    }
}
