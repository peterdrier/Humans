namespace Humans.Application.Interfaces;

/// <summary>
/// Persistence for camp image blobs. Implementations are owned by the
/// infrastructure layer (filesystem under <c>wwwroot</c> in production; an
/// in-memory stub in tests). The service layer never touches
/// <c>System.IO</c> directly so the <c>Humans.Application</c> project can
/// stay framework-free.
/// </summary>
public interface ICampImageStorage
{
    /// <summary>
    /// Save the image bytes. Returns the storage path (relative to
    /// <c>wwwroot</c>) the caller should persist on the <c>CampImage</c>
    /// record. The implementation is responsible for generating a unique
    /// file name and creating any intermediate directories.
    /// </summary>
    Task<string> SaveImageAsync(
        Guid campId,
        Stream fileStream,
        string contentType,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes the image at the given storage path if it exists. No-op if
    /// the file does not exist.
    /// </summary>
    void DeleteImage(string storagePath);

    /// <summary>
    /// Bulk delete of images (used during camp deletion). Each path is
    /// resolved independently; a missing file is not an error.
    /// </summary>
    void DeleteImages(IEnumerable<string> storagePaths);
}
