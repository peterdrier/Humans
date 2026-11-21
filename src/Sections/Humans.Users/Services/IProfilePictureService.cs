using Humans.Base.Interfaces;

namespace Humans.Users.Services;

/// <summary>The database content type is the GDPR gate: a stale file of an anonymized profile is never served.</summary>
internal interface IProfilePictureService : IApplicationService
{
    Task<(byte[] Data, string ContentType)?> GetProfilePictureAsync(
        Guid userId,
        CancellationToken ct = default);
}
