using NodaTime;

namespace Humans.Application.Interfaces.Containers;

public interface IContainerService
{
    Task<IReadOnlyList<ContainerDto>> GetByCampAsync(Guid campId, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerDto>> GetAllAsync(CancellationToken ct = default);
    Task<ContainerDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ContainerDto> CreateAsync(ContainerData data, CancellationToken ct = default);
    Task<ContainerDto> UpdateAsync(Guid id, ContainerData data, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    // Placement
    Task<ContainerPlacementDto?> GetPlacementAsync(Guid containerId, int year, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerPlacementDto>> GetPlacementsByYearAsync(int year, CancellationToken ct = default);
    Task<ContainerPlacementDto> SavePlacementAsync(Guid containerId, int year, string geoJson, CancellationToken ct = default);
    Task ClearPlacementAsync(Guid containerId, int year, CancellationToken ct = default);
    Task<ContainerPlacementDto> UpsertPlacementMetadataAsync(ContainerPlacementData data, CancellationToken ct = default);

    /// <summary>
    /// Org-wide admin overview of containers for a year, grouped by camp.
    /// Per-camp barrio groupings include all camps with a season in the year
    /// (even if the camp has no containers yet). Org-level containers belong
    /// to the well-known Organization camp (SystemCampIds.Organization).
    /// </summary>
    Task<ContainerAdminOverview> GetAdminOverviewAsync(int year, CancellationToken ct = default);

    /// <summary>
    /// Returns true iff the user may save/clear a container's placement —
    /// either as a map admin (caller passes that flag) or as the camp lead
    /// of the container's owning camp during an open placement phase.
    /// Org-level containers (CampId == SystemCampIds.Organization) can only
    /// be placed by map admins.
    /// </summary>
    Task<bool> CanUserPlaceContainerAsync(Guid userId, ContainerDto container, bool isMapAdmin, CancellationToken ct = default);
}

public record ContainerAdminOverview(
    int Year,
    IReadOnlyList<ContainerWithPlacement> OrgContainers,
    IReadOnlyList<ContainerCampGroup> CampGroups);

public record ContainerCampGroup(
    Guid CampId,
    string CampName,
    string CampSlug,
    IReadOnlyList<ContainerWithPlacement> Containers);

public record ContainerWithPlacement(ContainerDto Container, ContainerPlacementDto? Placement);

public interface IContainerImageStorage
{
    Task<string> SaveImageAsync(Guid containerId, Stream stream, string contentType, ContainerImageKind kind, CancellationToken ct = default);
    void DeleteImage(string storagePath);
}

public enum ContainerImageKind { Main, Placement }

public record ContainerImageUpload(Stream Content, string ContentType, string FileName, long Length);

public record ContainerDto(
    Guid Id,
    Guid CampId,
    string Name,
    string? Description,
    string? ImageStoragePath,
    string? ImageContentType,
    string? ImageFileName,
    Instant CreatedAt,
    Instant UpdatedAt
);

public record ContainerPlacementDto(
    Guid ContainerId,
    int Year,
    string? LocationGeoJson,
    string? PlacementNotes,
    string? PlacementImageStoragePath,
    string? PlacementImageContentType,
    string? PlacementImageFileName,
    Instant CreatedAt,
    Instant UpdatedAt
);

public record ContainerData(
    Guid CampId,
    string Name,
    string? Description,
    ContainerImageUpload? MainImage = null,
    bool RemoveMainImage = false
);

public record ContainerPlacementData(
    Guid ContainerId,
    int Year,
    string? LocationGeoJson,
    string? PlacementNotes,
    ContainerImageUpload? PlacementImage = null,
    bool RemovePlacementImage = false
);
