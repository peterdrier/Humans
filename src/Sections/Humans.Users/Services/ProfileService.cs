using Humans.Users.Data.Repositories;
using Humans.Users.Contracts;
using Humans.Base.Interfaces;

namespace Humans.Users.Services;

internal sealed class ProfileService(IUserRepository userRepository,
    IUserService userService,
    IFileStorage fileStorage) : IProfilePictureService
{
    /// <summary>
    /// Answers from the cached UserInfo, which every profile save and merge refreshes. GDPR gate: a cleared content
    /// type leaves no file key, so nothing is served from disk once cleared.
    /// </summary>
    public async Task<(byte[] Data, string ContentType)?> GetProfilePictureAsync(
        Guid profileId, CancellationToken ct = default)
    {
        var profile = (await userService.GetAllRawUserInfosAsync(ct))
            .Select(info => info.Profile)
            .FirstOrDefault(p => p?.Id == profileId);
        if (profile is not { ProfilePictureFileKey: { } key, ProfilePictureContentType: { } contentType })
        {
            return null;
        }

        var fsBytes = await fileStorage.TryReadAsync(key, ct);
        return fsBytes is not null ? (fsBytes, contentType) : null;
    }

    public Task<IReadOnlyList<ProfileChildRowsWithoutUser>> GetProfileChildRowsWithoutUserAsync(CancellationToken ct = default) =>
        userRepository.GetProfileChildRowsWithoutUserAsync(ct);

    public Task<int> FillProfileChildUserIdsAsync(CancellationToken ct = default) =>
        userRepository.FillProfileChildUserIdsAsync(ct);
}
