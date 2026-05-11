using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CitiPlanning;
using Humans.Application.Interfaces.Containers;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Services.Containers;

public sealed class ContainerService : IContainerService
{
    private static readonly string[] AllowedContentTypes = ["image/jpeg", "image/png", "image/webp"];
    private const long MaxImageBytes = 10 * 1024 * 1024;

    private readonly IContainerRepository _repo;
    private readonly IContainerImageStorage _imageStorage;
    private readonly ICampService _campService;
    private readonly ICityPlanningService _cityPlanningService;
    private readonly IClock _clock;

    public ContainerService(
        IContainerRepository repo,
        IContainerImageStorage imageStorage,
        ICampService campService,
        ICityPlanningService cityPlanningService,
        IClock clock)
    {
        _repo = repo;
        _imageStorage = imageStorage;
        _campService = campService;
        _cityPlanningService = cityPlanningService;
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

    public async Task<ContainerDto> CreateAsync(ContainerData data, CancellationToken ct = default)
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
            container.ImageStoragePath = await _imageStorage.SaveImageAsync(id, data.MainImage.Content, data.MainImage.ContentType, ContainerImageKind.Main, ct);
            container.ImageContentType = data.MainImage.ContentType;
            container.ImageFileName = data.MainImage.FileName;
        }

        var created = await _repo.AddAsync(container, ct);
        return ToDto(created);
    }

    public async Task<ContainerDto> UpdateAsync(Guid id, ContainerData data, CancellationToken ct = default)
    {
        ValidateImage(data.MainImage);

        var container = await _repo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("Container not found.");

        container.Name = data.Name;
        container.Description = data.Description;
        container.UpdatedAt = _clock.GetCurrentInstant();

        if (data.RemoveMainImage && container.ImageStoragePath is not null)
        {
            _imageStorage.DeleteImage(container.ImageStoragePath);
            container.ImageStoragePath = null;
            container.ImageContentType = null;
            container.ImageFileName = null;
        }
        else if (data.MainImage is not null)
        {
            if (container.ImageStoragePath is not null)
            {
                _imageStorage.DeleteImage(container.ImageStoragePath);
            }
            container.ImageStoragePath = await _imageStorage.SaveImageAsync(id, data.MainImage.Content, data.MainImage.ContentType, ContainerImageKind.Main, ct);
            container.ImageContentType = data.MainImage.ContentType;
            container.ImageFileName = data.MainImage.FileName;
        }

        var updated = await _repo.UpdateAsync(container, ct);
        return ToDto(updated);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var container = await _repo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("Container not found.");

        if (container.ImageStoragePath is not null)
        {
            _imageStorage.DeleteImage(container.ImageStoragePath);
        }

        // Placement-image files for this container are not cleaned up on
        // deletion (no per-container scan method on the repo). Placement rows
        // are removed by the repo's cascade; orphaned files on disk are
        // tolerated at this scale — see docs/sections/Containers.md.
        await _repo.DeleteAsync(id, ct);
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

    public async Task<ContainerPlacementDto> SavePlacementAsync(Guid containerId, int year, string geoJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(geoJson))
        {
            throw new ArgumentException("GeoJson must not be empty.", nameof(geoJson));
        }

        var container = await _repo.GetByIdAsync(containerId, ct)
            ?? throw new InvalidOperationException("Container not found.");

        var now = _clock.GetCurrentInstant();
        var existing = await _repo.GetPlacementAsync(containerId, year, ct);
        var placement = new ContainerPlacement
        {
            ContainerId = containerId,
            Year = year,
            LocationGeoJson = geoJson,
            PlacementNotes = existing?.PlacementNotes,
            PlacementImageStoragePath = existing?.PlacementImageStoragePath,
            PlacementImageContentType = existing?.PlacementImageContentType,
            PlacementImageFileName = existing?.PlacementImageFileName,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
        };

        await _repo.UpsertPlacementAsync(placement, ct);
        return ToPlacementDto(placement);
    }

    public async Task ClearPlacementAsync(Guid containerId, int year, CancellationToken ct = default)
    {
        var existing = await _repo.GetPlacementAsync(containerId, year, ct);
        if (existing is null) return;

        var hasMetadata = !string.IsNullOrEmpty(existing.PlacementNotes)
            || existing.PlacementImageStoragePath is not null;

        if (!hasMetadata)
        {
            await _repo.DeletePlacementAsync(containerId, year, ct);
            return;
        }

        existing.LocationGeoJson = null;
        existing.UpdatedAt = _clock.GetCurrentInstant();
        await _repo.UpsertPlacementAsync(existing, ct);
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

        var orgContainers = allContainers
            .Where(c => c.CampId == SystemCampIds.Organization)
            .Select(Compose)
            .ToList();

        var byCampId = allContainers
            .Where(c => c.CampId != SystemCampIds.Organization)
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

        return new ContainerAdminOverview(year, orgContainers, campGroups);
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
