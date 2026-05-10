using Humans.Application.Interfaces.Containers;
using Microsoft.Extensions.Hosting;

namespace Humans.Infrastructure.Services;

public sealed class ContainerImageStorage : IContainerImageStorage
{
    private readonly string _root;

    public ContainerImageStorage(IHostEnvironment environment)
    {
        // Resolve wwwroot via ContentRootPath (Infrastructure does not
        // reference Microsoft.AspNetCore.Hosting; same approach as
        // FileSystemFileStorage).
        _root = Path.Combine(environment.ContentRootPath, "wwwroot");
    }

    public async Task<string> SaveImageAsync(
        Guid containerId,
        Stream fileStream,
        string contentType,
        ContainerImageKind kind,
        CancellationToken ct = default)
    {
        var ext = contentType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => throw new InvalidOperationException("Unsupported content type.")
        };

        var prefix = kind switch
        {
            ContainerImageKind.Main => "main",
            ContainerImageKind.Placement => "placement",
            _ => throw new InvalidOperationException("Unknown image kind.")
        };

        var storedFileName = $"{prefix}-{Guid.NewGuid()}{ext}";
        var relativePath = Path.Combine("uploads", "containers", containerId.ToString(), storedFileName);
        var fullPath = Path.Combine(_root, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var stream = new FileStream(fullPath, FileMode.Create);
        await fileStream.CopyToAsync(stream, ct);

        return relativePath;
    }

    public void DeleteImage(string storagePath)
    {
        var fullPath = Path.Combine(_root, storagePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }
}
