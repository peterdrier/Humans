namespace Humans.Users.Contracts;

/// <summary>
/// The <c>IFileStorage</c> key layout for profile pictures — the example
/// <c>IFileStorage</c>'s own contract documents (<c>uploads/profile-pictures/{id}.jpg</c>).
/// </summary>
/// <remarks>
/// It has two owners: the section's picture service composes the key on write, and
/// <c>AccountDeletionService</c> — the cross-section deletion orchestrator that §I keeps in
/// <c>Humans.Application</c> — composes the same key to remove the file on GDPR anonymization.
/// It sat in <c>Humans.Application.Storage</c> for that reason until G5 lane 4b-2l re-measured
/// it: a Base consumer forces the *leaf*, not Base residency (design §15 step 5b), and the leaf
/// is where the profile-picture vocabulary belongs. Duplicating it would fork the one string
/// both halves must agree on.
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
