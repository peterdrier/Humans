using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Containers;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.Containers;

public sealed class ContainerService(
    IContainerRepository repo,
    IFileStorage fileStorage,
    ICampServiceRead campService,
    IAuditLogService auditLog,
    IClock clock) : IContainerService
{
    private static readonly HashSet<string> AllowedContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };
    private static readonly HashSet<string> AllowedImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };
    private const long MaxImageBytes = 10 * 1024 * 1024;

    public async Task<IReadOnlyList<ContainerDto>> GetByCampAsync(Guid campId, CancellationToken ct = default)
    {
        var containers = await repo.GetByCampAsync(campId, ct);
        return containers.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<ContainerDto>> GetAllAsync(CancellationToken ct = default)
    {
        var containers = await repo.GetAllAsync(ct);
        return containers.Select(ToDto).ToList();
    }

    public async Task<ContainerDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var container = await repo.GetByIdAsync(id, ct);
        return container is null ? null : ToDto(container);
    }

    public async Task<ContainerDto> CreateAsync(ContainerData data, Guid actorUserId, CancellationToken ct = default)
    {
        ValidateImage(data.MainImage);

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

        if (data.MainImage is not null)
        {
            container.ImageStoragePath = await SaveImageAsync(id, data.MainImage, ct);
            container.ImageContentType = data.MainImage.ContentType;
            container.ImageFileName = data.MainImage.FileName;
        }

        var created = await repo.AddAsync(container, ct);
        await auditLog.LogAsync(
            AuditAction.ContainerCreated, nameof(Container), created.Id,
            $"Created container '{created.Name}'",
            actorUserId,
            relatedEntityId: created.CampId, relatedEntityType: nameof(Camp));
        return ToDto(created);
    }

    public async Task<ContainerDto> UpdateAsync(Guid id, ContainerData data, Guid actorUserId, CancellationToken ct = default)
    {
        ValidateImage(data.MainImage);

        var container = await repo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("Container not found.");

        container.Name = data.Name;
        container.Description = data.Description;
        container.UpdatedAt = clock.GetCurrentInstant();

        if (data.RemoveMainImage && container.ImageStoragePath is not null)
        {
            await fileStorage.DeleteAsync(container.ImageStoragePath, ct);
            container.ImageStoragePath = null;
            container.ImageContentType = null;
            container.ImageFileName = null;
        }
        else if (data.MainImage is not null)
        {
            if (container.ImageStoragePath is not null)
            {
                await fileStorage.DeleteAsync(container.ImageStoragePath, ct);
            }
            container.ImageStoragePath = await SaveImageAsync(id, data.MainImage, ct);
            container.ImageContentType = data.MainImage.ContentType;
            container.ImageFileName = data.MainImage.FileName;
        }

        var updated = await repo.UpdateAsync(container, ct);
        await auditLog.LogAsync(
            AuditAction.ContainerUpdated, nameof(Container), updated.Id,
            $"Updated container '{updated.Name}'",
            actorUserId,
            relatedEntityId: updated.CampId, relatedEntityType: nameof(Camp));
        return ToDto(updated);
    }

    public async Task DeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var container = await repo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("Container not found.");

        if (container.ImageStoragePath is not null)
        {
            await fileStorage.DeleteAsync(container.ImageStoragePath, ct);
        }

        // Orphaned placement-image files tolerated at this scale; see docs/sections/Containers.md.
        await repo.DeleteAsync(id, ct);

        await auditLog.LogAsync(
            AuditAction.ContainerDeleted, nameof(Container), container.Id,
            $"Deleted container '{container.Name}'",
            actorUserId,
            relatedEntityId: container.CampId, relatedEntityType: nameof(Camp));
    }

    public async Task<ContainerPlacementDto?> GetPlacementAsync(Guid containerId, int year, CancellationToken ct = default)
    {
        var placement = await repo.GetPlacementAsync(containerId, year, ct);
        return placement is null ? null : ToPlacementDto(placement);
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
            AuditAction.ContainerPlacementSaved, nameof(ContainerPlacement), containerId,
            $"Placed container on map for {year}",
            actorUserId,
            relatedEntityId: containerId, relatedEntityType: nameof(Container));
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
            AuditAction.ContainerPlacementCleared, nameof(ContainerPlacement), containerId,
            $"Cleared container placement for {year}",
            actorUserId,
            relatedEntityId: containerId, relatedEntityType: nameof(Container));
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

        if (removeImage && placement.PlacementImageStoragePath is not null)
        {
            await fileStorage.DeleteAsync(placement.PlacementImageStoragePath, ct);
            placement.PlacementImageStoragePath = null;
            placement.PlacementImageContentType = null;
            placement.PlacementImageFileName = null;
        }
        else if (image is not null)
        {
            if (placement.PlacementImageStoragePath is not null)
            {
                await fileStorage.DeleteAsync(placement.PlacementImageStoragePath, ct);
            }
            placement.PlacementImageStoragePath = await SaveImageAsync(containerId, image, ct);
            placement.PlacementImageContentType = image.ContentType;
            placement.PlacementImageFileName = image.FileName;
        }

        placement.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpsertPlacementAsync(placement, ct);

        await auditLog.LogAsync(
            AuditAction.ContainerPlacementNotesUpdated, nameof(ContainerPlacement), containerId,
            $"Updated placement notes for {year}",
            actorUserId,
            relatedEntityId: containerId, relatedEntityType: nameof(Container));

        return ToPlacementDto(placement);
    }

    public async Task<ContainerAdminOverview> GetAdminOverviewAsync(int year, CancellationToken ct = default)
    {
        var allContainers = await repo.GetAllAsync(ct);
        var placements = await repo.GetPlacementsByYearAsync(year, ct);
        var placementByContainerId = placements.ToDictionary(p => p.ContainerId, p => p);

        var camps = await campService.GetCampsForYearAsync(year, ct);

        ContainerWithPlacement Compose(Container c) => new(
            ToDto(c),
            placementByContainerId.TryGetValue(c.Id, out var p) ? ToPlacementDto(p) : null);

        var byCampId = allContainers
            .GroupBy(c => c.CampId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var campGroups = camps
            .Select(camp => new ContainerCampGroup(
                camp.Id,
                camp.Seasons.First(s => s.Year == year).Name,
                camp.Slug,
                byCampId.TryGetValue(camp.Id, out var cs)
                    ? cs.Select(Compose).ToList()
                    : []))
            .ToList();

        return new ContainerAdminOverview(year, campGroups);
    }

    private static void ValidateImage(ContainerImageUpload? image)
    {
        if (image is null) return;
        if (!AllowedContentTypes.Contains(image.ContentType))
        {
            throw new InvalidOperationException("Only JPEG, PNG, and WebP images are allowed.");
        }
        if (image.Length > MaxImageBytes)
        {
            throw new InvalidOperationException("Image must be under 10 MB.");
        }
        // Security: extension whitelist prevents image/jpeg + .html (static middleware would serve as HTML).
        var ext = Path.GetExtension(image.FileName);
        if (!AllowedImageExtensions.Contains(ext))
        {
            throw new InvalidOperationException(
                "Image filename must end in .jpg, .jpeg, .png, or .webp.");
        }
    }

    private async Task<string> SaveImageAsync(Guid containerId, ContainerImageUpload image, CancellationToken ct)
    {
        var ext = Path.GetExtension(image.FileName);
        var key = $"uploads/containers/{containerId}/{Guid.NewGuid()}{ext}";
        await fileStorage.SaveAsync(key, image.Content, ct);
        return key;
    }

    private static ContainerDto ToDto(Container c) => new(
        c.Id,
        c.CampId,
        c.Name,
        c.Description,
        c.ImageStoragePath is not null ? $"/{c.ImageStoragePath}" : null,
        c.ImageContentType,
        c.ImageFileName,
        c.CreatedAt,
        c.UpdatedAt);

    private static ContainerPlacementDto ToPlacementDto(ContainerPlacement p) => new(
        p.ContainerId,
        p.Year,
        p.LocationGeoJson,
        p.PlacementNotes,
        p.PlacementImageStoragePath is not null ? $"/{p.PlacementImageStoragePath}" : null,
        p.PlacementImageContentType,
        p.PlacementImageFileName,
        p.CreatedAt,
        p.UpdatedAt);
}
