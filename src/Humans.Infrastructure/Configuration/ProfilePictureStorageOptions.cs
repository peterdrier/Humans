namespace Humans.Infrastructure.Configuration;

/// <summary>
/// Options binding for filesystem profile-picture storage. See
/// <c>Humans.Infrastructure.Services.Profiles.FileSystemProfilePictureStore</c>
/// for usage. Introduced in issue nobodies-collective/Humans#527.
/// </summary>
public sealed class ProfilePictureStorageOptions
{
    /// <summary>
    /// Configuration section name (<c>Storage:ProfilePictures</c>).
    /// </summary>
    public const string SectionName = "Storage:ProfilePictures";

    /// <summary>
    /// Directory in which to store profile pictures. Resolved relative to
    /// <c>IHostEnvironment.ContentRootPath</c> when not absolute. Defaults to
    /// <c>App_Data/profile-pictures</c>.
    /// </summary>
    public string Path { get; set; } = System.IO.Path.Combine("App_Data", "profile-pictures");
}
