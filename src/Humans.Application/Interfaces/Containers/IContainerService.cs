using NodaTime;

namespace Humans.Application.Interfaces.Containers;

public interface IContainerService : IApplicationService
{
    Task<IReadOnlyList<ContainerDto>> GetByCampAsync(Guid campId, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerDto>> GetAllAsync(CancellationToken ct = default);
    Task<ContainerDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ContainerDto> CreateAsync(ContainerData data, Guid actorUserId, CancellationToken ct = default);
    Task<ContainerDto> UpdateAsync(Guid id, ContainerData data, Guid actorUserId, CancellationToken ct = default);
    Task DeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default);

    // Placement
    Task<ContainerPlacementDto?> GetPlacementAsync(Guid containerId, int year, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerPlacementDto>> GetPlacementsByYearAsync(int year, CancellationToken ct = default);
    Task<ContainerPlacementDto> SavePlacementAsync(Guid containerId, int year, string geoJson, Guid actorUserId, CancellationToken ct = default);
    Task ClearPlacementAsync(Guid containerId, int year, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Update a placement's notes and/or sketch image. Requires the placement row
    /// to already exist (i.e., the container has been placed for the given year).
    /// Throws <see cref="InvalidOperationException"/> if no placement row exists.
    /// </summary>
    Task<ContainerPlacementDto> UpdatePlacementNotesAsync(
        Guid containerId,
        int year,
        string? notes,
        ContainerImageUpload? image,
        bool removeImage,
        Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Org-wide admin overview of containers for a year, grouped by camp.
    /// Includes every camp with a season in the year, even if the camp
    /// currently has no containers.
    /// </summary>
    Task<ContainerAdminOverview> GetAdminOverviewAsync(int year, CancellationToken ct = default);

}

public record ContainerAdminOverview(
    int Year,
    IReadOnlyList<ContainerCampGroup> CampGroups);

public record ContainerCampGroup(
    Guid CampId,
    string CampName,
    string CampSlug,
    IReadOnlyList<ContainerWithPlacement> Containers);

public record ContainerWithPlacement(ContainerDto Container, ContainerPlacementDto? Placement);

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
