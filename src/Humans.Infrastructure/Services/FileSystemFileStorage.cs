using Humans.Application.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Filesystem-backed <see cref="IFileStorage"/> rooted at the application's
/// wwwroot. Keys are appended directly to the root so they map one-to-one
/// onto static-file URLs (e.g. key <c>uploads/camps/{id}/{guid}.jpg</c>
/// is served at <c>/uploads/camps/{id}/{guid}.jpg</c>).
/// </summary>
/// <remarks>
/// Production deployments persist <c>wwwroot/uploads/</c> via a Coolify
/// volume mount. Subpaths under uploads/ that should NOT be publicly served
/// (e.g. profile pictures, which need the GDPR gate in
/// <c>ProfileService.GetProfilePictureAsync</c>) are excluded from
/// <c>app.UseStaticFiles()</c> via dedicated middleware in <c>Program.cs</c>.
/// </remarks>
public sealed class FileSystemFileStorage : IFileStorage
{
    private readonly string _root;
    private readonly ILogger<FileSystemFileStorage> _logger;

    public FileSystemFileStorage(
        IHostEnvironment environment,
        ILogger<FileSystemFileStorage> logger)
    {
        // wwwroot is conventional for an ASP.NET Core app; Infrastructure
        // does not reference Microsoft.AspNetCore.Hosting so we resolve it
        // from ContentRootPath instead of IWebHostEnvironment.WebRootPath.
        _root = Path.Combine(environment.ContentRootPath, "wwwroot");
        _logger = logger;
    }

    public async Task SaveAsync(string key, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var fullPath = ResolveAbsolute(key);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(stream, ct);
        }

        try
        {
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch (Exception moveEx)
        {
            _logger.LogWarning(moveEx,
                "Failed to rename temp file {TempPath} to {FinalPath}; cleaning up",
                tempPath, fullPath);
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException cleanupEx)
            {
                _logger.LogWarning(cleanupEx,
                    "Failed to clean up temp file {TempPath} after a failed rename to {FinalPath}",
                    tempPath, fullPath);
            }
            throw;
        }
    }

    public Task SaveAsync(string key, byte[] content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return SaveAsync(key, new MemoryStream(content, writable: false), ct);
    }

    public async Task<byte[]?> TryReadAsync(string key, CancellationToken ct = default)
    {
        var fullPath = ResolveAbsolute(key);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            return await File.ReadAllBytesAsync(fullPath, ct);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to read file at {Path}", fullPath);
            return null;
        }
    }

    // Synchronous I/O wrapped as Task for the IFileStorage contract.
    // File.Exists / File.Delete block for the duration of the syscall;
    // at our scale (<5 deletes per request, single server) the impact
    // is negligible. If this ever moves to a high-throughput path,
    // wrap in Task.Run.
    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var fullPath = ResolveAbsolute(key);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
        return Task.CompletedTask;
    }

    private string ResolveAbsolute(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Storage key must be provided.", nameof(key));
        }
        if (Path.IsPathRooted(key))
        {
            throw new ArgumentException(
                $"Storage key must be relative to wwwroot, got '{key}'.", nameof(key));
        }
        // Reject any segment that is exactly "..". Substring matches like
        // "foo..bar" are filename characters, not traversal.
        var segments = key.Split('/', '\\');
        if (segments.Any(s => string.Equals(s, "..", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"Storage key must not contain parent-directory segments, got '{key}'.", nameof(key));
        }

        return Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
    }
}
