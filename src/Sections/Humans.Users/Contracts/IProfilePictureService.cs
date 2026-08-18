using Humans.Base.Interfaces;

namespace Humans.Users.Contracts;

/// <summary>
/// Owns profile-picture filesystem IO and the DB content-type gate that
/// prevents stale anonymized files from being served.
/// </summary>
public interface IProfilePictureService : IApplicationService
{
    Task<(byte[] Data, string ContentType)?> GetProfilePictureAsync(
        Guid profileId,
        CancellationToken ct = default);

    Task<ProfilePictureMigrationSnapshot> GetProfilePictureMigrationSnapshotAsync(
        CancellationToken ct = default);

    Task SetProfilePictureAsync(
        Guid userId,
        byte[] pictureData,
        string contentType,
        CancellationToken ct = default);
}
