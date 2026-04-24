namespace Humans.Application.Interfaces.Profiles;

/// <summary>
/// Persistence for profile picture blobs. Phase 1 of the picture-storage
/// refactor (issue nobodies-collective/Humans#527): adds filesystem storage
/// alongside the existing <c>Profile.ProfilePictureData</c> / <c>ProfilePictureContentType</c>
/// columns. Phase 2 will drop the columns; phase 3 switches to cloud storage;
/// phase 4 makes the backend per-environment configurable.
/// </summary>
/// <remarks>
/// Implementations are owned by the infrastructure layer so the
/// <c>Humans.Application</c> project stays framework-free. The interface is
/// intentionally local to profile pictures — do not generalize into a shared
/// blob store.
/// </remarks>
public interface IProfilePictureStore
{
    /// <summary>
    /// Reads the picture bytes for <paramref name="profileId"/>. Returns
    /// <c>null</c> when no picture is present in the store. Implementations
    /// must not throw for missing files — a miss is a normal outcome.
    /// </summary>
    Task<(byte[] Data, string ContentType)?> TryReadAsync(
        Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Writes the picture bytes atomically for <paramref name="profileId"/>.
    /// Overwrites any existing file. Implementations should write to a
    /// temporary path and rename so readers never observe a partial file.
    /// </summary>
    Task WriteAsync(
        Guid profileId, byte[] data, string contentType, CancellationToken ct = default);

    /// <summary>
    /// Deletes the picture for <paramref name="profileId"/> from the store.
    /// No-op if no picture is present.
    /// </summary>
    Task DeleteAsync(Guid profileId, CancellationToken ct = default);
}
