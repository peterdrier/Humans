using Humans.Application.Interfaces;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Filesystem-backed <see cref="ICampImageStorage"/>. Images are stored
/// under <c>wwwroot/uploads/camps/{campId}/</c> so ASP.NET Core's static
/// file middleware can serve them directly.
/// </summary>
public sealed class CampImageStorage : ICampImageStorage
{
    private static string Root => Path.Combine("wwwroot");

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

        var storedFileName = $"{Guid.NewGuid()}{ext}";
        var relativePath = Path.Combine("uploads", "camps", campId.ToString(), storedFileName);
        var fullPath = Path.Combine(Root, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var stream = new FileStream(fullPath, FileMode.Create);
        await fileStream.CopyToAsync(stream, ct);

        return relativePath;
    }

    public void DeleteImage(string storagePath)
    {
        var fullPath = Path.Combine(Root, storagePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    public void DeleteImages(IEnumerable<string> storagePaths)
    {
        foreach (var path in storagePaths)
        {
            DeleteImage(path);
        }
    }
}
