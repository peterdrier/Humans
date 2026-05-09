using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CitiPlanning;
using Humans.Application.Interfaces.Containers;
using Humans.Application.Interfaces.Repositories;
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

    public async Task<IReadOnlyList<ContainerDto>> GetByCampAsync(Guid campId, int year, CancellationToken ct = default)
    {
        var containers = await _repo.GetByCampAsync(campId, year, ct);
        return containers.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<ContainerDto>> GetOrgByYearAsync(int year, CancellationToken ct = default)
    {
        var containers = await _repo.GetOrgByYearAsync(year, ct);
        return containers.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<ContainerDto>> GetAllByYearAsync(int year, CancellationToken ct = default)
    {
        var containers = await _repo.GetAllByYearAsync(year, ct);
        return containers.Select(ToDto).ToList();
    }

    public async Task<ContainerDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var container = await _repo.GetByIdAsync(id, ct);
        return container is null ? null : ToDto(container);
    }

    public async Task<ContainerDto> CreateAsync(ContainerData data, CancellationToken ct = default)
    {
        ValidateImages(data.MainImage, data.PlacementImage);

        var now = _clock.GetCurrentInstant();
        var id = Guid.NewGuid();
        var container = new Container
        {
            Id = id,
            CampId = data.CampId,
            Year = data.Year,
            Name = data.Name,
            Description = data.Description,
            PlacementNotes = data.PlacementNotes,
            CreatedAt = now,
            UpdatedAt = now
        };

        if (data.MainImage is not null)
        {
            container.ImageStoragePath = await _imageStorage.SaveImageAsync(id, data.MainImage.Content, data.MainImage.ContentType, ContainerImageKind.Main, ct);
            container.ImageContentType = data.MainImage.ContentType;
            container.ImageFileName = data.MainImage.FileName;
        }

        if (data.PlacementImage is not null)
        {
            container.PlacementImageStoragePath = await _imageStorage.SaveImageAsync(id, data.PlacementImage.Content, data.PlacementImage.ContentType, ContainerImageKind.Placement, ct);
            container.PlacementImageContentType = data.PlacementImage.ContentType;
            container.PlacementImageFileName = data.PlacementImage.FileName;
        }

        var created = await _repo.AddAsync(container, ct);
        return ToDto(created);
    }

    public async Task<ContainerDto> UpdateAsync(Guid id, ContainerData data, CancellationToken ct = default)
    {
        ValidateImages(data.MainImage, data.PlacementImage);

        var container = await _repo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("Container not found.");

        container.Name = data.Name;
        container.Description = data.Description;
        container.PlacementNotes = data.PlacementNotes;
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

        if (data.RemovePlacementImage && container.PlacementImageStoragePath is not null)
        {
            _imageStorage.DeleteImage(container.PlacementImageStoragePath);
            container.PlacementImageStoragePath = null;
            container.PlacementImageContentType = null;
            container.PlacementImageFileName = null;
        }
        else if (data.PlacementImage is not null)
        {
            if (container.PlacementImageStoragePath is not null)
            {
                _imageStorage.DeleteImage(container.PlacementImageStoragePath);
            }
            container.PlacementImageStoragePath = await _imageStorage.SaveImageAsync(id, data.PlacementImage.Content, data.PlacementImage.ContentType, ContainerImageKind.Placement, ct);
            container.PlacementImageContentType = data.PlacementImage.ContentType;
            container.PlacementImageFileName = data.PlacementImage.FileName;
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

        if (container.PlacementImageStoragePath is not null)
        {
            _imageStorage.DeleteImage(container.PlacementImageStoragePath);
        }

        await _repo.DeleteAsync(id, ct);
    }

    public async Task<ContainerDto> SavePlacementAsync(Guid id, string geoJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(geoJson))
        {
            throw new ArgumentException("GeoJson must not be empty.", nameof(geoJson));
        }

        var container = await _repo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("Container not found.");

        container.LocationGeoJson = geoJson;
        container.UpdatedAt = _clock.GetCurrentInstant();

        var updated = await _repo.UpdateAsync(container, ct);
        return ToDto(updated);
    }

    public async Task ClearPlacementAsync(Guid id, CancellationToken ct = default)
    {
        var container = await _repo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("Container not found.");

        container.LocationGeoJson = null;
        container.UpdatedAt = _clock.GetCurrentInstant();

        await _repo.UpdateAsync(container, ct);
    }

    public async Task<ContainerAdminOverview> GetAdminOverviewAsync(int year, CancellationToken ct = default)
    {
        var allContainers = await _repo.GetAllByYearAsync(year, ct);
        var campDisplay = await _campService.GetCampDisplayDataForYearAsync(year, ct);

        var byCampId = allContainers
            .Where(c => c.CampId is not null)
            .GroupBy(c => c.CampId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var orgContainers = allContainers
            .Where(c => c.CampId is null)
            .Select(ToDto)
            .ToList();

        var campGroups = campDisplay
            .Select(kvp => new ContainerCampGroup(
                kvp.Key,
                kvp.Value.Name,
                kvp.Value.CampSlug,
                byCampId.TryGetValue(kvp.Key, out var cs)
                    ? cs.Select(ToDto).ToList()
                    : []))
            .ToList();

        return new ContainerAdminOverview(year, orgContainers, campGroups);
    }

    public async Task<bool> CanUserPlaceContainerAsync(
        Guid userId, ContainerDto container, bool isMapAdmin, CancellationToken ct = default)
    {
        if (isMapAdmin) return true;
        if (container.CampId is null) return false;

        var settings = await _cityPlanningService.GetSettingsAsync(ct);
        if (!settings.IsContainerPlacementOpen) return false;

        return await _campService.IsUserCampLeadAsync(userId, container.CampId.Value, ct);
    }

    private static void ValidateImages(ContainerImageUpload? mainImage, ContainerImageUpload? placementImage)
    {
        if (mainImage is not null && !AllowedContentTypes.Contains(mainImage.ContentType))
        {
            throw new InvalidOperationException("Only JPEG, PNG, and WebP images are allowed.");
        }
        if (mainImage is not null && mainImage.Length > MaxImageBytes)
        {
            throw new InvalidOperationException("Image must be under 10 MB.");
        }
        if (placementImage is not null && !AllowedContentTypes.Contains(placementImage.ContentType))
        {
            throw new InvalidOperationException("Only JPEG, PNG, and WebP images are allowed.");
        }
        if (placementImage is not null && placementImage.Length > MaxImageBytes)
        {
            throw new InvalidOperationException("Image must be under 10 MB.");
        }
    }

    private static ContainerDto ToDto(Container c) => new(
        c.Id,
        c.CampId,
        c.Year,
        c.Name,
        c.Description,
        c.ImageStoragePath is not null ? $"/{c.ImageStoragePath}" : null,
        c.ImageContentType,
        c.ImageFileName,
        c.LocationGeoJson,
        c.PlacementNotes,
        c.PlacementImageStoragePath is not null ? $"/{c.PlacementImageStoragePath}" : null,
        c.PlacementImageContentType,
        c.PlacementImageFileName,
        c.CreatedAt,
        c.UpdatedAt);
}
