namespace Humans.Application.Interfaces;

/// <summary>
/// Single, key-addressed file/blob persistence abstraction. The implementation
/// is rooted at the wwwroot directory so keys translate one-to-one to URL
/// paths under the public uploads area. Domain concerns (GDPR gates,
/// content-type validation, max sizes, image format whitelisting) live in
/// the consuming service, never in the store.
/// </summary>
/// <remarks>
/// Keys are forward-slash separated, relative to wwwroot. The owning section
/// chooses its prefix (e.g. <c>uploads/profile-pictures/{id}.jpg</c>,
/// <c>uploads/camps/{campId}/{guid}.jpg</c>). Path traversal segments
/// (<c>..</c>) and rooted paths are rejected.
/// </remarks>
public interface IFileStorage
{
    /// <summary>
    /// Atomically write <paramref name="content"/> to <paramref name="key"/>,
    /// overwriting any existing file. Implementations write to a temp sibling
    /// and rename so readers never observe a partial file.
    /// </summary>
    Task SaveAsync(string key, Stream content, CancellationToken ct = default);

    /// <summary>
    /// Convenience overload for callers that already have the bytes in memory.
    /// </summary>
    Task SaveAsync(string key, byte[] content, CancellationToken ct = default);

    /// <summary>
    /// Read the bytes at <paramref name="key"/>. Returns <c>null</c> when
    /// no file is present. Implementations must not throw for missing files —
    /// a miss is a normal outcome.
    /// </summary>
    Task<byte[]?> TryReadAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Delete the file at <paramref name="key"/> if it exists. No-op when
    /// the file is missing. IO failures (locked file, permissions) are
    /// propagated so the caller can decide how to handle them — relevant
    /// for GDPR deletion paths that need to log on failure.
    /// </summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
}
