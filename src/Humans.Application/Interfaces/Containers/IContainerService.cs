using NodaTime;

namespace Humans.Application.Interfaces.Containers;

public interface IContainerService
{
    Task<IReadOnlyList<ContainerDto>> GetByCampAsync(Guid campId, int year, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerDto>> GetOrgByYearAsync(int year, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerDto>> GetAllByYearAsync(int year, CancellationToken ct = default);
    Task<ContainerDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ContainerDto> CreateAsync(ContainerData data, CancellationToken ct = default);
    Task<ContainerDto> UpdateAsync(Guid id, ContainerData data, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<ContainerDto> SavePlacementAsync(Guid id, string geoJson, CancellationToken ct = default);
    Task ClearPlacementAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Org-wide admin overview of containers for a year, grouped by camp.
    /// Per-camp barrio groupings include all camps with a season in the year
    /// (even if the camp has no containers yet).
    /// </summary>
    Task<ContainerAdminOverview> GetAdminOverviewAsync(int year, CancellationToken ct = default);

    /// <summary>
    /// Returns true iff the user may save/clear a container's placement —
    /// either as a map admin (caller passes that flag) or as the camp lead
    /// of the container's owning camp during an open placement phase.
    /// </summary>
    Task<bool> CanUserPlaceContainerAsync(Guid userId, ContainerDto container, bool isMapAdmin, CancellationToken ct = default);
}

public record ContainerAdminOverview(
    int Year,
    IReadOnlyList<ContainerDto> OrgContainers,
    IReadOnlyList<ContainerCampGroup> CampGroups);

public record ContainerCampGroup(
    Guid CampId,
    string CampName,
    string CampSlug,
    IReadOnlyList<ContainerDto> Containers);

public interface IContainerImageStorage
{
    Task<string> SaveImageAsync(Guid containerId, Stream stream, string contentType, ContainerImageKind kind, CancellationToken ct = default);
    void DeleteImage(string storagePath);
}

public enum ContainerImageKind { Main, Placement }

public record ContainerImageUpload(Stream Content, string ContentType, string FileName, long Length);

public record ContainerDto(
    Guid Id,
    Guid? CampId,
    int Year,
    string Name,
    string? Description,
    string? ImageStoragePath,
    string? ImageContentType,
    string? ImageFileName,
    string? LocationGeoJson,
    string? PlacementNotes,
    string? PlacementImageStoragePath,
    string? PlacementImageContentType,
    string? PlacementImageFileName,
    Instant CreatedAt,
    Instant UpdatedAt
);

public record ContainerData(
    Guid? CampId,
    int Year,
    string Name,
    string? Description,
    string? PlacementNotes = null,
    ContainerImageUpload? MainImage = null,
    ContainerImageUpload? PlacementImage = null,
    bool RemoveMainImage = false,
    bool RemovePlacementImage = false
);
