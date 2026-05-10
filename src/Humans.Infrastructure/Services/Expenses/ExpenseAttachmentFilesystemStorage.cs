using Humans.Application.Interfaces.Expenses;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Services.Expenses;

/// <summary>
/// Stores expense attachment files on the local filesystem under the configured root.
/// </summary>
public sealed class ExpenseAttachmentFilesystemStorage : IExpenseAttachmentStorageService
{
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".jpg", ".jpeg", ".png", ".heic"
        };

    private readonly string _root;
    private readonly ILogger<ExpenseAttachmentFilesystemStorage> _logger;

    public ExpenseAttachmentFilesystemStorage(
        IOptions<ExpenseAttachmentFilesystemStorageOptions> options,
        ILogger<ExpenseAttachmentFilesystemStorage> logger)
    {
        _root = options.Value.Root;
        _logger = logger;
        Directory.CreateDirectory(_root);
    }

    public async Task<Guid> StoreAsync(
        Stream content, string extension, string contentType,
        CancellationToken ct = default)
    {
        ValidateExtension(extension);

        var id = Guid.NewGuid();
        var path = ResolvePath(id, extension);

        _logger.LogDebug("Storing expense attachment {Id}{Extension} at {Path}", id, extension, path);

        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        await content.CopyToAsync(fs, ct);

        return id;
    }

    public Task<Stream> OpenReadAsync(
        Guid id, string extension, CancellationToken ct = default)
    {
        ValidateExtension(extension);

        var path = ResolvePath(id, extension);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Expense attachment {id}{extension} not found.", path);
        }

        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(Guid id, string extension, CancellationToken ct = default)
    {
        ValidateExtension(extension);

        var path = ResolvePath(id, extension);

        // File.Delete is idempotent — does nothing if the file doesn't exist.
        File.Delete(path);

        return Task.CompletedTask;
    }

    private string ResolvePath(Guid id, string extension)
    {
        // Compose candidate path and canonicalize both sides.
        var candidate = Path.Combine(_root, $"{id}{extension}");
        var canonicalRoot = Path.GetFullPath(_root);
        var canonicalPath = Path.GetFullPath(candidate);

        if (!canonicalPath.StartsWith(canonicalRoot, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Path traversal detected: computed path '{canonicalPath}' escapes root '{canonicalRoot}'.",
                nameof(extension));
        }

        return canonicalPath;
    }

    private static void ValidateExtension(string extension)
    {
        if (!AllowedExtensions.Contains(extension))
        {
            throw new ArgumentException(
                $"Extension '{extension}' is not permitted. Allowed: {string.Join(", ", AllowedExtensions)}.",
                nameof(extension));
        }
    }
}
