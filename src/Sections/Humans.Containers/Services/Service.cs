using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Camps.Contracts;
using Humans.Containers.Contracts;
using Humans.Containers.Data;
using Humans.Containers.Domain;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Containers.Services;

internal sealed class Service(
    IContainerRepository repo,
    IFileStorage fileStorage,
    ICampServiceRead campService,
    IAuditLogService auditLog,
    IClock clock,
    IStringLocalizer<ContainersResource> localizer,
    ILogger<Service> logger) : IContainerService
{
    // Names travel into JS string templates and HTML on the map pages; ban the characters
    // that are ever token-significant there rather than trusting every sink to escape.
    private static readonly char[] InvalidNameChars = ['<', '>', '$'];

    private static readonly HashSet<string> AllowedContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };
    private static readonly HashSet<string> AllowedImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };
    private const long MaxImageBytes = 10 * 1024 * 1024;
    private const int MaxImagesPerContainer = 5;
    private const int MaxImageFileNameLength = 256;

    public async Task<IReadOnlyList<ContainerDto>> GetByCampAsync(Guid campId, CancellationToken ct = default)
    {
        var containers = await repo.GetByCampAsync(campId, ct);
        return await ToDtosAsync(containers, ct);
    }

    public async Task<IReadOnlyList<ContainerDto>> GetAllAsync(CancellationToken ct = default)
    {
        var containers = await repo.GetAllAsync(ct);
        return await ToDtosAsync(containers, ct);
    }

    public async Task<ContainerDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var container = await repo.GetByIdAsync(id, ct);
        if (container is null) return null;
        return ToDto(container, await repo.GetImagesAsync([id], ct));
    }

    public async Task<ContainerDto> CreateAsync(ContainerData data, Guid actorUserId, CancellationToken ct = default)
    {
        ValidateName(data.Name);

        var uploads = data.NewImages ?? [];
        foreach (var upload in uploads)
        {
            ValidateImage(upload);
        }
        ValidateImageCount(uploads.Count);

        var now = clock.GetCurrentInstant();
        var id = Guid.NewGuid();
        var container = new Container
        {
            Id = id,
            CampId = data.CampId,
            Name = data.Name,
            Description = data.Description,
            CreatedAt = now,
            UpdatedAt = now
        };

        var images = await SaveImagesAsync(id, uploads, firstSortOrder: 0, now, ct);
        var created = await repo.AddAsync(container, images, ct);

        await auditLog.LogAsync(
            AuditAction.ContainerCreated, AuditEntityTypes.Container, created.Id,
            $"Created container '{created.Name}'",
            actorUserId,
            relatedEntityId: created.CampId, relatedEntityType: AuditEntityTypes.Camp);
        return ToDto(created, await repo.GetImagesAsync([id], ct));
    }

    public async Task<ContainerDto> UpdateAsync(Guid id, ContainerData data, Guid actorUserId, CancellationToken ct = default)
    {
        ValidateName(data.Name);

        var uploads = data.NewImages ?? [];
        foreach (var upload in uploads)
        {
            ValidateImage(upload);
        }

        var container = await repo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("Container not found.");

        var now = clock.GetCurrentInstant();
        container.Name = data.Name;
        container.Description = data.Description;
        container.UpdatedAt = now;

        var removeIds = data.RemoveImageIds ?? [];
        var existing = await repo.GetImagesAsync([id], ct);
        var removed = existing.Where(i => removeIds.Contains(i.Id)).ToList();
        var removeLegacy = container.ImageStoragePath is not null && removeIds.Contains(Guid.Empty);

        var keptCount = existing.Count - removed.Count
            + (container.ImageStoragePath is not null && !removeLegacy ? 1 : 0);
        ValidateImageCount(keptCount + uploads.Count);

        var obsoletePaths = removed.Select(i => i.StoragePath).ToList();
        if (removeLegacy)
        {
            obsoletePaths.Add(container.ImageStoragePath!);
            container.ImageStoragePath = null;
            container.ImageContentType = null;
            container.ImageFileName = null;
        }

        var nextSortOrder = existing.Except(removed).Select(i => i.SortOrder + 1).DefaultIfEmpty(0).Max();
        var images = await SaveImagesAsync(id, uploads, nextSortOrder, now, ct);

        var updated = await repo.UpdateAsync(container, images, removed.Select(i => i.Id).ToList(), ct);
        await auditLog.LogAsync(
            AuditAction.ContainerUpdated, AuditEntityTypes.Container, updated.Id,
            $"Updated container '{updated.Name}'",
            actorUserId,
            relatedEntityId: updated.CampId, relatedEntityType: AuditEntityTypes.Camp);
        foreach (var path in obsoletePaths.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await fileStorage.DeleteAsync(path, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete image file {StoragePath} after container {ContainerId} was updated", path, id);
            }
        }
        return ToDto(updated, await repo.GetImagesAsync([id], ct));
    }

    public async Task DeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var container = await repo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("Container not found.");

        var imagePaths = (await repo.GetImagesAsync([id], ct))
            .Select(image => image.StoragePath)
            .ToList();
        if (container.ImageStoragePath is not null)
            imagePaths.Add(container.ImageStoragePath);

        // Orphaned placement-image files tolerated at this scale; see Docs/Containers.md.
        await repo.DeleteAsync(id, ct);

        await auditLog.LogAsync(
            AuditAction.ContainerDeleted, AuditEntityTypes.Container, container.Id,
            $"Deleted container '{container.Name}'",
            actorUserId,
            relatedEntityId: container.CampId, relatedEntityType: AuditEntityTypes.Camp);

        foreach (var path in imagePaths.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await fileStorage.DeleteAsync(path, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete image file {StoragePath} after container {ContainerId} was deleted", path, id);
            }
        }
    }

    public async Task<IReadOnlyList<ContainerPlacementDto>> GetPlacementsByYearAsync(int year, CancellationToken ct = default)
    {
        var placements = await repo.GetPlacementsByYearAsync(year, ct);
        return placements.Select(ToPlacementDto).ToList();
    }

    public async Task<ContainerPlacementDto> SavePlacementAsync(Guid containerId, int year, string geoJson, Guid actorUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(geoJson))
        {
            throw new ArgumentException("GeoJson must not be empty.", nameof(geoJson));
        }

        var placement = await repo.SavePlacementGeometryAsync(
            containerId, year, geoJson, clock.GetCurrentInstant(), ct);
        await auditLog.LogAsync(
            AuditAction.ContainerPlacementSaved, AuditEntityTypes.ContainerPlacement, containerId,
            $"Placed container on map for {year}",
            actorUserId,
            relatedEntityId: containerId, relatedEntityType: AuditEntityTypes.Container);
        return ToPlacementDto(placement);
    }

    public async Task ClearPlacementAsync(Guid containerId, int year, Guid actorUserId, CancellationToken ct = default)
    {
        var existing = await repo.GetPlacementAsync(containerId, year, ct);
        if (existing is null) return;

        var hasMetadata = !string.IsNullOrEmpty(existing.PlacementNotes)
            || existing.PlacementImageStoragePath is not null;

        if (!hasMetadata)
        {
            await repo.DeletePlacementAsync(containerId, year, ct);
        }
        else
        {
            existing.LocationGeoJson = null;
            existing.UpdatedAt = clock.GetCurrentInstant();
            await repo.UpsertPlacementAsync(existing, ct);
        }

        await auditLog.LogAsync(
            AuditAction.ContainerPlacementCleared, AuditEntityTypes.ContainerPlacement, containerId,
            $"Cleared container placement for {year}",
            actorUserId,
            relatedEntityId: containerId, relatedEntityType: AuditEntityTypes.Container);
    }

    public async Task<ContainerPlacementDto> UpdatePlacementNotesAsync(
        Guid containerId,
        int year,
        string? notes,
        ContainerImageUpload? image,
        bool removeImage,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        ValidateImage(image);

        var placement = await repo.GetPlacementAsync(containerId, year, ct)
            ?? throw new InvalidOperationException("Placement not found. Place the container on the map first.");

        placement.PlacementNotes = string.IsNullOrWhiteSpace(notes) ? null : notes;

        var previousImagePath = placement.PlacementImageStoragePath;
        if (removeImage && previousImagePath is not null)
        {
            placement.PlacementImageStoragePath = null;
            placement.PlacementImageContentType = null;
            placement.PlacementImageFileName = null;
        }
        else if (image is not null)
        {
            placement.PlacementImageStoragePath = await SaveImageAsync(containerId, image, ct);
            placement.PlacementImageContentType = image.ContentType;
            placement.PlacementImageFileName = DisplayFileName(image.FileName);
        }

        placement.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpsertPlacementAsync(placement, ct);

        await auditLog.LogAsync(
            AuditAction.ContainerPlacementNotesUpdated, AuditEntityTypes.ContainerPlacement, containerId,
            $"Updated placement notes for {year}",
            actorUserId,
            relatedEntityId: containerId, relatedEntityType: AuditEntityTypes.Container);

        // The previous file remains usable until the new metadata is committed.
        if (previousImagePath is not null
            && !string.Equals(previousImagePath, placement.PlacementImageStoragePath, StringComparison.Ordinal))
        {
            try
            {
                await fileStorage.DeleteAsync(previousImagePath, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete superseded placement image {StoragePath} for container {ContainerId}, year {Year}",
                    previousImagePath, containerId, year);
            }
        }

        return ToPlacementDto(placement);
    }

    public async Task<ContainerAdminOverview> GetAdminOverviewAsync(int year, CancellationToken ct = default)
    {
        var allContainers = await repo.GetAllAsync(ct);
        var placements = await repo.GetPlacementsByYearAsync(year, ct);
        var placementByContainerId = placements.ToDictionary(p => p.ContainerId, p => p);
        var imagesByContainerId = await LoadImagesAsync(allContainers, ct);

        var camps = await campService.GetCampsForYearAsync(year, ct);

        ContainerWithPlacement Compose(Container c) => new(
            ToDto(c, imagesByContainerId.GetValueOrDefault(c.Id, [])),
            placementByContainerId.TryGetValue(c.Id, out var p) ? ToPlacementDto(p) : null);

        var byCampId = allContainers
            .GroupBy(c => c.CampId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var campGroups = camps
            .Select(camp => new ContainerCampGroup(
                camp.Id,
                camp.Seasons.First(s => s.Year == year).Name,
                byCampId.TryGetValue(camp.Id, out var cs)
                    ? cs.Select(Compose).ToList()
                    : []))
            .ToList();

        return new ContainerAdminOverview(year, campGroups);
    }

    private void ValidateName(string name)
    {
        if (name.IndexOfAny(InvalidNameChars) >= 0)
        {
            throw new InvalidOperationException(localizer["Containers_Error_InvalidName"]);
        }
    }

    private void ValidateImageCount(int total)
    {
        if (total > MaxImagesPerContainer)
        {
            throw new InvalidOperationException(localizer["Containers_Error_TooManyImages", MaxImagesPerContainer]);
        }
    }

    private void ValidateImage(ContainerImageUpload? image)
    {
        if (image is null) return;
        if (!AllowedContentTypes.Contains(image.ContentType))
        {
            throw new InvalidOperationException(localizer["Containers_Error_ImageType"]);
        }
        if (image.Length > MaxImageBytes)
        {
            throw new InvalidOperationException(localizer["Containers_Error_ImageTooLarge"]);
        }
        // Security: extension whitelist prevents image/jpeg + .html (static middleware would serve as HTML).
        var fileName = DisplayFileName(image.FileName);
        if (fileName.Length > MaxImageFileNameLength)
        {
            throw new InvalidOperationException(localizer["Containers_Error_ImageFileNameLength", MaxImageFileNameLength]);
        }

        var ext = Path.GetExtension(fileName);
        if (!AllowedImageExtensions.Contains(ext))
        {
            throw new InvalidOperationException(localizer["Containers_Error_ImageExtension"]);
        }
    }

    private async Task<string> SaveImageAsync(Guid containerId, ContainerImageUpload image, CancellationToken ct)
    {
        var ext = Path.GetExtension(DisplayFileName(image.FileName));
        var key = $"uploads/containers/{containerId}/{Guid.NewGuid()}{ext}";
        await fileStorage.SaveAsync(key, image.Content, ct);
        return key;
    }

    private async Task<IReadOnlyCollection<ContainerImage>> SaveImagesAsync(
        Guid containerId,
        IReadOnlyList<ContainerImageUpload> uploads,
        int firstSortOrder,
        Instant now,
        CancellationToken ct)
    {
        if (uploads.Count == 0) return [];

        var rows = new List<ContainerImage>(uploads.Count);
        for (var i = 0; i < uploads.Count; i++)
        {
            var upload = uploads[i];
            rows.Add(new ContainerImage
            {
                Id = Guid.NewGuid(),
                ContainerId = containerId,
                StoragePath = await SaveImageAsync(containerId, upload, ct),
                ContentType = upload.ContentType,
                FileName = DisplayFileName(upload.FileName),
                SortOrder = firstSortOrder + i,
                CreatedAt = now,
            });
        }
        return rows;
    }

    private static string DisplayFileName(string fileName) =>
        fileName.Split('/', '\\').Last();

    private async Task<IReadOnlyList<ContainerDto>> ToDtosAsync(
        IReadOnlyList<Container> containers, CancellationToken ct)
    {
        var imagesByContainerId = await LoadImagesAsync(containers, ct);
        return containers
            .Select(c => ToDto(c, imagesByContainerId.GetValueOrDefault(c.Id, [])))
            .ToList();
    }

    private async Task<Dictionary<Guid, List<ContainerImage>>> LoadImagesAsync(
        IReadOnlyList<Container> containers, CancellationToken ct)
    {
        var images = await repo.GetImagesAsync(containers.Select(c => c.Id).ToList(), ct);
        return images
            .GroupBy(i => i.ContainerId)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.SortOrder).ToList());
    }

    private static ContainerDto ToDto(Container c, IReadOnlyList<ContainerImage> images)
    {
        var gallery = new List<ContainerImageDto>(images.Count + 1);
        // Pre-nobodies-collective/Humans#797 containers still hold their single image in the containers columns;
        // Guid.Empty addresses it so callers see one uniform, removable gallery.
        if (c.ImageStoragePath is not null)
        {
            gallery.Add(new ContainerImageDto(Guid.Empty, $"/{c.ImageStoragePath}", c.ImageFileName));
        }
        gallery.AddRange(images
            .OrderBy(i => i.SortOrder)
            .Select(i => new ContainerImageDto(i.Id, $"/{i.StoragePath}", i.FileName)));

        return new ContainerDto(c.Id, c.CampId, c.Name, c.Description, gallery, c.CreatedAt, c.UpdatedAt);
    }

    private static ContainerPlacementDto ToPlacementDto(ContainerPlacement p) => new(
        p.ContainerId,
        p.Year,
        p.LocationGeoJson,
        p.PlacementNotes,
        p.PlacementImageStoragePath is not null ? $"/{p.PlacementImageStoragePath}" : null,
        p.PlacementImageFileName,
        p.CreatedAt,
        p.UpdatedAt);
}
