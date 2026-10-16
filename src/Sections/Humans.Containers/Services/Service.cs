using System.Text.Json;
using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Camps.Contracts;
using Humans.Containers.Contracts;
using Humans.Containers.Data;
using Humans.Containers.Domain;
using NodaTime;

namespace Humans.Containers.Services;

internal sealed class Service(
    IContainerRepository repo,
    IFileStorage fileStorage,
    ICampServiceRead campService,
    IAuditLogService auditLog,
    IClock clock,
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

    public async Task<ContainerMutationResult<ContainerDto>> CreateAsync(ContainerData data, Guid actorUserId, CancellationToken ct = default)
    {
        if (ValidateName(data.Name) is { } nameError) return nameError.ToResult<ContainerDto>();

        var uploads = data.NewImages ?? [];
        foreach (var upload in uploads)
        {
            if (ValidateImage(upload) is { } imageError) return imageError.ToResult<ContainerDto>();
        }
        if (ValidateImageCount(uploads.Count) is { } countError) return countError.ToResult<ContainerDto>();

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
        return new(ToDto(created, await repo.GetImagesAsync([id], ct)));
    }

    public async Task<ContainerMutationResult<ContainerDto>> UpdateAsync(Guid id, ContainerData data, Guid actorUserId, CancellationToken ct = default)
    {
        if (ValidateName(data.Name) is { } nameError) return nameError.ToResult<ContainerDto>();

        var uploads = data.NewImages ?? [];
        foreach (var upload in uploads)
        {
            if (ValidateImage(upload) is { } imageError) return imageError.ToResult<ContainerDto>();
        }

        var container = await repo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("Container not found.");

        var now = clock.GetCurrentInstant();
        var removeIds = data.RemoveImageIds ?? [];
        var existing = await repo.GetImagesAsync([id], ct);
        var removed = existing.Where(i => removeIds.Contains(i.Id)).ToList();
        var removeLegacy = container.ImageStoragePath is not null && removeIds.Contains(Guid.Empty);

        var keptCount = existing.Count - removed.Count
            + (container.ImageStoragePath is not null && !removeLegacy ? 1 : 0);
        if (ValidateImageCount(keptCount + uploads.Count) is { } countError) return countError.ToResult<ContainerDto>();

        container.Name = data.Name;
        container.Description = data.Description;
        container.UpdatedAt = now;

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
        await DeleteObsoleteImagesAsync(obsoletePaths, id, "updated");
        return new(ToDto(updated, await repo.GetImagesAsync([id], ct)));
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

        await DeleteObsoleteImagesAsync(imagePaths, id, "deleted");
    }

    private async Task DeleteObsoleteImagesAsync(IEnumerable<string> paths, Guid containerId, string operation)
    {
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await fileStorage.DeleteAsync(path, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete image file {StoragePath} after container {ContainerId} was {Operation}",
                    path, containerId, operation);
            }
        }
    }

    public async Task<IReadOnlyList<ContainerPlacementDto>> GetPlacementsByYearAsync(int year, CancellationToken ct = default)
    {
        var placements = await repo.GetPlacementsByYearAsync(year, ct);
        return placements.Select(ToPlacementDto).ToList();
    }

    public async Task<ContainerMutationResult<ContainerPlacementDto>> SavePlacementAsync(Guid containerId, int year, string geoJson, Guid actorUserId, CancellationToken ct = default)
    {
        if (!IsValidContainerPlacementGeoJson(geoJson))
            return new(null, "Containers_Error_InvalidPlacementGeoJson");

        var placement = await repo.SavePlacementGeometryAsync(
            containerId, year, geoJson, clock.GetCurrentInstant(), ct);
        await auditLog.LogAsync(
            AuditAction.ContainerPlacementSaved, AuditEntityTypes.ContainerPlacement, containerId,
            $"Placed container on map for {year}",
            actorUserId,
            relatedEntityId: containerId, relatedEntityType: AuditEntityTypes.Container);
        return new(ToPlacementDto(placement));
    }

    private static bool IsValidContainerPlacementGeoJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!string.Equals(root.GetProperty("type").GetString(), "Feature", StringComparison.Ordinal)) return false;
            var geometry = root.GetProperty("geometry");
            if (!string.Equals(geometry.GetProperty("type").GetString(), "Polygon", StringComparison.Ordinal)) return false;
            var properties = root.GetProperty("properties");
            if (!IsFiniteNumber(properties.GetProperty("center_lng"))
                || !IsFiniteNumber(properties.GetProperty("center_lat"))
                || !IsFiniteNumber(properties.GetProperty("rotation_degrees"))) return false;
            if (properties.GetProperty("center_lng").GetDouble() is < -180 or > 180
                || properties.GetProperty("center_lat").GetDouble() is < -90 or > 90) return false;

            var coordinates = geometry.GetProperty("coordinates");
            if (coordinates.GetArrayLength() == 0) return false;
            foreach (var ring in coordinates.EnumerateArray())
            {
                if (ring.GetArrayLength() < 4) return false;
                foreach (var position in ring.EnumerateArray())
                {
                    if (position.GetArrayLength() < 2 || position.EnumerateArray().Any(n => !IsFiniteNumber(n))) return false;
                    if (position[0].GetDouble() is < -180 or > 180 || position[1].GetDouble() is < -90 or > 90) return false;
                }
                if (!ring[0].EnumerateArray().Select(n => n.GetDouble())
                    .SequenceEqual(ring[ring.GetArrayLength() - 1].EnumerateArray().Select(n => n.GetDouble()))) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }

        static bool IsFiniteNumber(JsonElement value) =>
            value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number);
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

    public async Task<ContainerMutationResult<ContainerPlacementDto>> UpdatePlacementNotesAsync(
        Guid containerId,
        int year,
        string? notes,
        ContainerImageUpload? image,
        bool removeImage,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        if (ValidateImage(image) is { } imageError) return imageError.ToResult<ContainerPlacementDto>();

        var placement = await repo.GetPlacementAsync(containerId, year, ct);
        if (placement is null) return new(null, "Containers_Error_PlacementNotFound");

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

        return new(ToPlacementDto(placement));
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

    private static ValidationError? ValidateName(string name) => name.IndexOfAny(InvalidNameChars) >= 0
        ? new("Containers_Error_InvalidName") : null;

    private static ValidationError? ValidateImageCount(int total) => total > MaxImagesPerContainer
        ? new("Containers_Error_TooManyImages", MaxImagesPerContainer) : null;

    private static ValidationError? ValidateImage(ContainerImageUpload? image)
    {
        if (image is null) return null;
        if (!AllowedContentTypes.Contains(image.ContentType))
            return new("Containers_Error_ImageType");
        if (image.Length > MaxImageBytes)
            return new("Containers_Error_ImageTooLarge");
        // Security: extension whitelist prevents image/jpeg + .html from being served as HTML.
        var fileName = DisplayFileName(image.FileName);
        if (fileName.Length > MaxImageFileNameLength)
            return new("Containers_Error_ImageFileNameLength", MaxImageFileNameLength);
        return !AllowedImageExtensions.Contains(Path.GetExtension(fileName))
            ? new("Containers_Error_ImageExtension") : null;
    }

    private sealed record ValidationError(string Key, params object[] Args)
    {
        public ContainerMutationResult<T> ToResult<T>() where T : class => new(null, Key, Args);
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
        try
        {
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
        }
        catch
        {
            // No database write has started; only these new files belong to the failed batch.
            foreach (var row in rows)
            {
                try
                {
                    await fileStorage.DeleteAsync(row.StoragePath, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to delete staged image file {StoragePath} after container {ContainerId} upload failed",
                        row.StoragePath, containerId);
                }
            }
            throw;
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
