using Humans.Application.Interfaces.Profiles;
using Humans.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Services.Profiles;

/// <summary>
/// Filesystem-backed <see cref="IProfilePictureStore"/>. Phase 1 of the
/// picture-storage refactor (issue nobodies-collective/Humans#527).
/// </summary>
/// <remarks>
/// Layout: one file per profile at
/// <c>{ConfiguredRoot}/{profileId}.{ext}</c>. Writes go to a temporary
/// <c>.tmp</c> sibling and are renamed into place so readers never observe a
/// partial file.
/// </remarks>
public sealed class FileSystemProfilePictureStore : IProfilePictureStore
{
    private readonly string _rootPath;
    private readonly ILogger<FileSystemProfilePictureStore> _logger;

    public FileSystemProfilePictureStore(
        IOptions<ProfilePictureStorageOptions> options,
        IHostEnvironment environment,
        ILogger<FileSystemProfilePictureStore> logger)
    {
        var configured = options.Value.Path;
        _rootPath = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured);
        _logger = logger;
    }

    public async Task<(byte[] Data, string ContentType)?> TryReadAsync(
        Guid profileId, CancellationToken ct = default)
    {
        var match = FindExistingFile(profileId);
        if (match is null)
        {
            return null;
        }

        var (fullPath, contentType) = match.Value;
        try
        {
            var bytes = await File.ReadAllBytesAsync(fullPath, ct);
            return (bytes, contentType);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex,
                "Failed to read profile picture from filesystem for {ProfileId} at {Path}",
                profileId, fullPath);
            return null;
        }
    }

    public async Task WriteAsync(
        Guid profileId, byte[] data, string contentType, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        var ext = ContentTypeToExtension(contentType);

        Directory.CreateDirectory(_rootPath);

        // Remove any existing file with a different extension so the
        // resolver doesn't return stale content.
        DeleteOtherExtensions(profileId, keepExtension: ext);

        var finalPath = Path.Combine(_rootPath, $"{profileId}{ext}");
        var tempPath = Path.Combine(_rootPath, $"{profileId}{ext}.{Guid.NewGuid():N}.tmp");

        await File.WriteAllBytesAsync(tempPath, data, ct);

        try
        {
            // File.Move with overwrite is atomic on Windows and POSIX
            // filesystems so readers never see a partial file.
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            // Clean up temp file if rename failed. Best-effort: we still
            // rethrow the original move failure to the caller below, but a
            // failure here only leaks a temp file we don't otherwise need
            // — log so an operator can spot disk-pressure / permission
            // issues that would otherwise be silent.
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException cleanupEx)
            {
                _logger.LogWarning(cleanupEx,
                    "Failed to clean up temp profile picture file {TempPath} after a failed rename",
                    tempPath);
            }
            throw;
        }
    }

    public Task DeleteAsync(Guid profileId, CancellationToken ct = default)
    {
        DeleteOtherExtensions(profileId, keepExtension: null);
        return Task.CompletedTask;
    }

    private (string FullPath, string ContentType)? FindExistingFile(Guid profileId)
    {
        if (!Directory.Exists(_rootPath))
        {
            return null;
        }

        foreach (var (ext, contentType) in SupportedExtensions)
        {
            var candidate = Path.Combine(_rootPath, $"{profileId}{ext}");
            if (File.Exists(candidate))
            {
                return (candidate, contentType);
            }
        }

        return null;
    }

    private void DeleteOtherExtensions(Guid profileId, string? keepExtension)
    {
        if (!Directory.Exists(_rootPath))
        {
            return;
        }

        foreach (var (ext, _) in SupportedExtensions)
        {
            if (keepExtension is not null &&
                string.Equals(ext, keepExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = Path.Combine(_rootPath, $"{profileId}{ext}");
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to delete profile picture file for {ProfileId} at {Path}",
                        profileId, path);
                }
            }
        }
    }

    private static string ContentTypeToExtension(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => throw new InvalidOperationException(
            $"Unsupported profile picture content type '{contentType}'.")
    };

    /// <summary>
    /// Extension/content-type pairs the store knows how to persist. Kept in
    /// sync with <see cref="Humans.Web.Helpers.ProfilePictureProcessor"/>
    /// output formats.
    /// </summary>
    private static readonly (string Extension, string ContentType)[] SupportedExtensions =
    [
        (".jpg", "image/jpeg"),
        (".png", "image/png"),
        (".webp", "image/webp"),
    ];
}
