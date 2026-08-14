namespace Humans.Application.Storage;

/// <summary>
/// The <see cref="Interfaces.IFileStorage"/> key layout for profile pictures — the example
/// <c>IFileStorage</c>'s own contract documents (<c>uploads/profile-pictures/{id}.jpg</c>).
/// </summary>
/// <remarks>
/// It sat in <c>Humans.Application.Services.Profiles</c> and would have moved into
/// <c>Humans.Users</c> with the rest of the section (nobodies-collective/Humans#866, G5 lane 2,
/// PR B) except that it has two owners: the section's picture service composes the key on write,
/// and <c>AccountDeletionService</c> — the cross-section deletion orchestrator that §I keeps in
/// <c>Humans.Application</c> — composes the same key to remove the file on GDPR anonymization.
/// Taking it into the section would leave Base unable to spell the key it deletes; duplicating it
/// forks the one string both halves must agree on. Its signature names <c>Guid</c> and
/// <c>string</c> and nothing the section owns, which is the test that keeps a helper in Base
/// (design §15 step 6's Shell-helper rule, applied one layer down).
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
