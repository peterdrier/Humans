namespace Humans.Users.Contracts;

/// <summary>
/// The one key both the picture service (on write) and <c>AccountDeletionService</c> (on GDPR anonymization)
/// compose, so they can't disagree.
/// </summary>
public static class ProfilePictureStorageKeys
{
    // Program.cs 404s this subpath, so reads go through the picture service and its GDPR gate.
    public static string ProfilePictureKey(Guid profileId, string contentType) =>
        $"uploads/profile-pictures/{profileId}{ExtensionFromContentType(contentType)}";

    public static string ExtensionFromContentType(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => string.Empty
    };
}
