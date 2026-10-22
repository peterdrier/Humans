using Humans.Base.Interfaces;

namespace Humans.Users.Contracts;

/// <summary>The database content type is the GDPR gate: a stale file of an anonymized profile is never served.</summary>
public interface IProfilePictureService : IApplicationService
{
    Task<(byte[] Data, string ContentType)?> GetProfilePictureAsync(
        Guid profileId,
        CancellationToken ct = default);

    Task<ProfilePictureMigrationSnapshot> GetProfilePictureMigrationSnapshotAsync(
        CancellationToken ct = default);
}
