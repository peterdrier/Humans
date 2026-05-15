using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Containers;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.Containers;

public sealed class ContainerService : IContainerService
{
    private static readonly HashSet<string> AllowedContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };
    private static readonly HashSet<string> AllowedImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };
    private const long MaxImageBytes = 10 * 1024 * 1024;

    private readonly IContainerRepository _repo;
    private readonly IFileStorage _fileStorage;
    private readonly ICampService _campService;
    private readonly IAuditLogService _auditLog;
    private readonly IClock _clock;

    public ContainerService(
        IContainerRepository repo,
        IFileStorage fileStorage,
        ICampService campService,
        IAuditLogService auditLog,
        IClock clock)
    {
        _repo = repo;
        _fileStorage = fileStorage;
        _campService = campService;
        _auditLog = auditLog;
        _clock = clock;
    }

    public async Task<IReadOnlyList<ContainerDto>> GetByCampAsync(Guid campId, CancellationToken ct = default)
    {
        var containers = await _repo.GetByCampAsync(campId, ct);
        return containers.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<ContainerDto>> GetAllAsync(CancellationToken ct = default)
    {
        var containers = await _repo.GetAllAsync(ct);
        return containers.Select(ToDto).ToList();
    }

    public async Task<ContainerDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var container = await _repo.GetByIdAsync(id, ct);
        return container is null ? null : ToDto(container);
    }

    public async Task<ContainerDto> CreateAsync(ContainerData data, Guid actorUserId, CancellationToken ct = default)
    {
        ValidateImage(data.MainImage);

        var now = _clock.GetCurrentInstant();
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

        var created = await _repo.AddAsync(container, ct);
        await _auditLog.LogAsync(
            AuditAction.ContainerCreated, nameof(Container), created.Id,
            $"Created container '{created.Name}'",
            actorUserId,
            relatedEntityId: created.CampId, relatedEntityType: nameof(Camp));
        return ToDto(created);
    }

    public async Task<ContainerDto> UpdateAsync(Guid id, ContainerData data, Guid actorUserId, CancellationToken ct = default)
    {
        ValidateImage(data.MainImage);

        var container = await _repo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("Container not found.");

        container.Name = data.Name;
        container.Description = data.Description;
        container.UpdatedAt = _clock.GetCurrentInstant();

        if (data.RemoveMainImage && container.ImageStoragePath is not null)
        {
            await _fileStorage.DeleteAsync(container.ImageStoragePath, ct);
            container.ImageStoragePath = null;
            container.ImageContentType = null;
            container.ImageFileName = null;
        }
        else if (data.MainImage is not null)
        {
            if (container.ImageStoragePath is not null)
            {
                await _fileStorage.DeleteAsync(container.ImageStoragePath, ct);
            }
            container.ImageStoragePath = await SaveImageAsync(id, data.MainImage, ct);
            container.ImageContentType = data.MainImage.ContentType;
            container.ImageFileName = data.MainImage.FileName;
        }

        var updated = await _repo.UpdateAsync(container, ct);
        await _auditLog.LogAsync(
            AuditAction.ContainerUpdated, nameof(Container), updated.Id,
            $"Updated container '{updated.Name}'",
            actorUserId,
            relatedEntityId: updated.CampId, relatedEntityType: nameof(Camp));
        return ToDto(updated);
    }

    public async Task DeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var container = await _repo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("Container not found.");

        if (container.ImageStoragePath is not null)
        {
            await _fileStorage.DeleteAsync(container.ImageStoragePath, ct);
        }

        // Placement-image files for this container are not cleaned up on
        // deletion (no per-container scan method on the repo). Placement rows
        // are removed by the repo's cascade; orphaned files on disk are
        // tolerated at this scale — see docs/sections/Containers.md.
        await _repo.DeleteAsync(id, ct);

        await _auditLog.LogAsync(
            AuditAction.ContainerDeleted, nameof(Container), container.Id,
            $"Deleted container '{container.Name}'",
            actorUserId,
            relatedEntityId: container.CampId, relatedEntityType: nameof(Camp));
    }

    public async Task<ContainerPlacementDto?> GetPlacementAsync(Guid containerId, int year, CancellationToken ct = default)
    {
        var placement = await _repo.GetPlacementAsync(containerId, year, ct);
        return placement is null ? null : ToPlacementDto(placement);
    }

    public async Task<IReadOnlyList<ContainerPlacementDto>> GetPlacementsByYearAsync(int year, CancellationToken ct = default)
    {
        var placements = await _repo.GetPlacementsByYearAsync(year, ct);
        return placements.Select(ToPlacementDto).ToList();
    }

    public async Task<ContainerPlacementDto> SavePlacementAsync(Guid containerId, int year, string geoJson, Guid actorUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(geoJson))
        {
            throw new ArgumentException("GeoJson must not be empty.", nameof(geoJson));
        }

        var placement = await _repo.SavePlacementGeometryAsync(
            containerId, year, geoJson, _clock.GetCurrentInstant(), ct);
        await _auditLog.LogAsync(
            AuditAction.ContainerPlacementSaved, nameof(ContainerPlacement), containerId,
            $"Placed container on map for {year}",
            actorUserId,
            relatedEntityId: containerId, relatedEntityType: nameof(Container));
        return ToPlacementDto(placement);
    }

    public async Task ClearPlacementAsync(Guid containerId, int year, Guid actorUserId, CancellationToken ct = default)
    {
        var existing = await _repo.GetPlacementAsync(containerId, year, ct);
        if (existing is null) return;

        var hasMetadata = !string.IsNullOrEmpty(existing.PlacementNotes)
            || existing.PlacementImageStoragePath is not null;

        if (!hasMetadata)
        {
            await _repo.DeletePlacementAsync(containerId, year, ct);
        }
        else
        {
            existing.LocationGeoJson = null;
            existing.UpdatedAt = _clock.GetCurrentInstant();
            await _repo.UpsertPlacementAsync(existing, ct);
        }

        await _auditLog.LogAsync(
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

        var placement = await _repo.GetPlacementAsync(containerId, year, ct)
            ?? throw new InvalidOperationException("Placement not found. Place the container on the map first.");

        placement.PlacementNotes = string.IsNullOrWhiteSpace(notes) ? null : notes;

        if (removeImage && placement.PlacementImageStoragePath is not null)
        {
            await _fileStorage.DeleteAsync(placement.PlacementImageStoragePath, ct);
            placement.PlacementImageStoragePath = null;
            placement.PlacementImageContentType = null;
            placement.PlacementImageFileName = null;
        }
        else if (image is not null)
        {
            if (placement.PlacementImageStoragePath is not null)
            {
                await _fileStorage.DeleteAsync(placement.PlacementImageStoragePath, ct);
            }
            placement.PlacementImageStoragePath = await SaveImageAsync(containerId, image, ct);
            placement.PlacementImageContentType = image.ContentType;
            placement.PlacementImageFileName = image.FileName;
        }

        placement.UpdatedAt = _clock.GetCurrentInstant();
        await _repo.UpsertPlacementAsync(placement, ct);

        await _auditLog.LogAsync(
            AuditAction.ContainerPlacementNotesUpdated, nameof(ContainerPlacement), containerId,
            $"Updated placement notes for {year}",
            actorUserId,
            relatedEntityId: containerId, relatedEntityType: nameof(Container));

        return ToPlacementDto(placement);
    }

    public async Task<ContainerAdminOverview> GetAdminOverviewAsync(int year, CancellationToken ct = default)
    {
        var allContainers = await _repo.GetAllAsync(ct);
        var placements = await _repo.GetPlacementsByYearAsync(year, ct);
        var placementByContainerId = placements.ToDictionary(p => p.ContainerId, p => p);

        var camps = await _campService.GetCampsForYearAsync(year, ct);

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
        // Filename extension must also be on the whitelist — a client could
        // pass MIME validation with image/jpeg but supply a .html filename,
        // and static-file middleware would then serve the upload as HTML.
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
        await _fileStorage.SaveAsync(key, image.Content, ct);
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
