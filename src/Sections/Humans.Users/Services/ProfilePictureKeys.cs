using Humans.Users.Contracts;

namespace Humans.Users.Services;

/// <summary>
/// Resolves the <c>IFileStorage</c> key a profile's picture lives at. Each upload gets a fresh
/// versioned key so a replacement never overwrites the previous bytes before metadata commits;
/// profiles that predate versioning (null stored key) still resolve the legacy deterministic key.
/// </summary>
internal static class ProfilePictureKeys
{
    public static string NewVersioned(Guid profileId, string contentType) =>
        $"uploads/profile-pictures/{profileId}-{Guid.NewGuid():N}{ProfilePictureStorageKeys.ExtensionFromContentType(contentType)}";

    public static string? Resolve(Guid profileId, string? contentType, string? storedKey) =>
        contentType is null
            ? null
            : storedKey ?? ProfilePictureStorageKeys.ProfilePictureKey(profileId, contentType);
}
