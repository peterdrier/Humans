using System.Collections.Concurrent;
using Humans.Application.Interfaces.Camps;

namespace Humans.Application.Tests.Infrastructure;

/// <summary>
/// In-memory <see cref="ICampImageStorage"/> test double. Records calls so
/// tests can assert behavior without touching the filesystem.
/// </summary>
internal sealed class InMemoryCampImageStorage : ICampImageStorage
{
    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public async Task<string> SaveImageAsync(
        Guid campId,
        Stream fileStream,
        string contentType,
        CancellationToken ct = default)
    {
        var ext = contentType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => throw new InvalidOperationException("Unsupported content type.")
        };

        var relativePath = Path.Combine("uploads", "camps", campId.ToString(), $"{Guid.NewGuid()}{ext}")
            .Replace('\\', '/');

        using var ms = new MemoryStream();
        await fileStream.CopyToAsync(ms, ct);
        Files[relativePath] = ms.ToArray();
        return relativePath;
    }

    public void DeleteImage(string storagePath) => Files.TryRemove(storagePath, out _);

    public void DeleteImages(IEnumerable<string> storagePaths)
    {
        foreach (var path in storagePaths)
        {
            DeleteImage(path);
        }
    }
}
