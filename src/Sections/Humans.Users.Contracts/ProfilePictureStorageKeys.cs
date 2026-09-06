namespace Humans.Users.Contracts;

/// <summary>
/// The <c>IFileStorage</c> key layout for profile pictures — the example
/// <c>IFileStorage</c>'s own contract documents (<c>uploads/profile-pictures/{id}.jpg</c>).
/// </summary>
/// <remarks>
/// Two owners: the section's picture service composes the key on write, and
/// <c>AccountDeletionService</c> (<c>Humans.Users/Services/</c>) composes the same key to
/// remove the file on GDPR anonymization. Duplicating it would fork the one string both
/// halves must agree on.
/// </remarks>
public static class ProfilePictureStorageKeys
{
    // Pictures live at uploads/profile-pictures/{id}{ext}; Program.cs 404s the subpath
    // so reads go through the profile-picture service path and its GDPR gate.
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
