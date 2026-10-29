using Humans.AuditLog.Contracts;
using NodaTime;

namespace Humans.Camps.Services;

internal sealed class CampImageGallery(
    ICampRepository repo,
    IFileStorage fileStorage,
    IClock clock,
    IAuditLogService auditLog,
    ILogger logger) : IApplicationService
{
    private static readonly HashSet<string> AllowedImageContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };
    private static readonly HashSet<string> AllowedImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };
    private const int MaxImageFileNameLength = 256;

    public async Task<CampImageUploadResult> UploadImageAsync(
        Guid campId, Stream fileStream, string fileName, string contentType, long length,
        Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var imageCount = await repo.CountImagesAsync(campId, cancellationToken);
        if (imageCount >= 5)
        {
            return CampImageUploadResult.Failure("Camps_Validation_ImageCount");
        }

        if (!AllowedImageContentTypes.Contains(contentType))
        {
            return CampImageUploadResult.Failure("Camps_Validation_ImageType");
        }

        if (length > 10 * 1024 * 1024)
        {
            return CampImageUploadResult.Failure("Camps_Validation_ImageSize");
        }

        // Security: extension whitelist prevents image/jpeg + .html (static middleware would serve as HTML).
        fileName = DisplayFileName(fileName);
        if (fileName.Length > MaxImageFileNameLength)
            return CampImageUploadResult.Failure("Camps_Validation_ImageFilenameLength");

        var ext = Path.GetExtension(fileName);
        if (!AllowedImageExtensions.Contains(ext))
        {
            return CampImageUploadResult.Failure("Camps_Validation_ImageExtension");
        }
        var storageKey = $"uploads/camps/{campId}/{Guid.NewGuid()}{ext}";
        await fileStorage.SaveAsync(storageKey, fileStream, cancellationToken);

        var image = new CampImage
        {
            Id = Guid.NewGuid(),
            CampId = campId,
            FileName = fileName,
            StoragePath = storageKey,
            ContentType = contentType,
            UploadedAt = clock.GetCurrentInstant()
        };

        try
        {
            await repo.AddImageAsync(image, cancellationToken);
        }
        catch
        {
            try
            {
                // A failed save may have committed: only remove an unreferenced file.
                if (await repo.GetImageForMutationAsync(image.Id, CancellationToken.None) is null)
                    await fileStorage.DeleteAsync(storageKey, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to verify or clean up camp image upload {ImageId} at {StoragePath}", image.Id, storageKey);
            }
            throw;
        }

        await auditLog.LogAsync(
            AuditAction.CampImageUploaded, nameof(CampImage), image.Id,
            $"Uploaded image '{fileName}'",
            actorUserId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        return CampImageUploadResult.Success(image);
    }

    private static string DisplayFileName(string fileName) => fileName.Split('/', '\\').Last();

    public async Task<CampUpdateResult> DeleteImageAsync(
        Guid scopedCampId, Guid imageId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var image = await repo.GetImageForMutationAsync(imageId, cancellationToken);
        if (image is null) return CampUpdateResult.Failure("Camps_Flash_ImageNotFound");
        if (image.CampId != scopedCampId)
        {
            return CampUpdateResult.Failure("Camps_Flash_ImageWrongCamp");
        }

        var result = await repo.DeleteImageAsync(imageId, cancellationToken);
        if (result is null) return CampUpdateResult.Failure("Camps_Flash_ImageNotFound");

        await auditLog.LogAsync(
            AuditAction.CampImageDeleted, nameof(CampImage), imageId,
            $"Deleted image {imageId}",
            actorUserId,
            relatedEntityId: result.Value.CampId, relatedEntityType: nameof(Camp));

        // Metadata and audit have committed; file cleanup must finish independently of the request.
        try
        {
            await fileStorage.DeleteAsync(result.Value.StoragePath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to delete camp image file at {StoragePath} for image {ImageId}; DB row already removed",
                result.Value.StoragePath, imageId);
        }

        return CampUpdateResult.Success();

    }

    public async Task ReorderImagesAsync(
        Guid campId, List<Guid> imageIdsInOrder, CancellationToken cancellationToken = default)
    {
        await repo.ReorderImagesAsync(campId, imageIdsInOrder, cancellationToken);
    }
}
