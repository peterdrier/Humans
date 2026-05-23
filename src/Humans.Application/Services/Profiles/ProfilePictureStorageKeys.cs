namespace Humans.Application.Services.Profiles;

internal static class ProfilePictureStorageKeys
{
    // Pictures live at uploads/profile-pictures/{id}{ext}; Program.cs 404s the subpath
    // so reads go through the profile-picture service path and its GDPR gate.
    internal static string ProfilePictureKey(Guid profileId, string contentType) =>
        $"uploads/profile-pictures/{profileId}{ExtensionFromContentType(contentType)}";

    internal static string ExtensionFromContentType(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => string.Empty
    };
}
