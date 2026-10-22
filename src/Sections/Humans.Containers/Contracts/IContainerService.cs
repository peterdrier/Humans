using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Containers.Contracts;

public interface IContainerService : IApplicationService
{
    Task<IReadOnlyList<ContainerDto>> GetByCampAsync(Guid campId, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerDto>> GetAllAsync(CancellationToken ct = default);
    Task<ContainerDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ContainerMutationResult<ContainerDto>> CreateAsync(ContainerData data, Guid actorUserId, CancellationToken ct = default);
    Task<ContainerMutationResult<ContainerDto>> UpdateAsync(Guid id, ContainerData data, Guid actorUserId, CancellationToken ct = default);
    Task DeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default);

    Task<IReadOnlyList<ContainerPlacementDto>> GetPlacementsByYearAsync(int year, CancellationToken ct = default);
    Task<ContainerMutationResult<ContainerPlacementDto>> SavePlacementAsync(Guid containerId, int year, string geoJson, Guid actorUserId, CancellationToken ct = default);
    Task ClearPlacementAsync(Guid containerId, int year, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Returns a refusal key when the container has no placement row for the year.</summary>
    Task<ContainerMutationResult<ContainerPlacementDto>> UpdatePlacementNotesAsync(
        Guid containerId,
        int year,
        string? notes,
        ContainerImageUpload? image,
        bool removeImage,
        Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>Includes every camp with a season in the year, even one with no containers.</summary>
    Task<ContainerAdminOverview> GetAdminOverviewAsync(int year, CancellationToken ct = default);

}

public record ContainerAdminOverview(
    int Year,
    IReadOnlyList<ContainerCampGroup> CampGroups);

public record ContainerCampGroup(
    Guid CampId,
    string CampName,
    IReadOnlyList<ContainerWithPlacement> Containers);

public record ContainerWithPlacement(ContainerDto Container, ContainerPlacementDto? Placement);

public record ContainerImageUpload(Stream Content, string ContentType, string FileName, long Length);

/// <summary>
/// <see cref="Id"/> is <see cref="Guid.Empty"/> for the legacy single image held in the <c>containers</c> columns.
/// </summary>
public record ContainerImageDto(Guid Id, string Url, string? FileName);

public record ContainerDto(
    Guid Id,
    Guid CampId,
    string Name,
    string? Description,
    IReadOnlyList<ContainerImageDto> Images,
    Instant CreatedAt,
    Instant UpdatedAt
);

public record ContainerPlacementDto(
    Guid ContainerId,
    int Year,
    string? LocationGeoJson,
    string? PlacementNotes,
    string? PlacementImageUrl,
    string? PlacementImageFileName,
    Instant CreatedAt,
    Instant UpdatedAt
)
{
    public bool IsPlaced => LocationGeoJson is not null;
    public bool HasPlacementInfo => !string.IsNullOrEmpty(PlacementNotes) || PlacementImageUrl is not null;
}

public record ContainerData(
    Guid CampId,
    string Name,
    string? Description,
    IReadOnlyList<ContainerImageUpload>? NewImages = null,
    IReadOnlyList<Guid>? RemoveImageIds = null
);

/// <summary>Successful owner DTO or a refusal key and arguments for caller localization.</summary>
public sealed record ContainerMutationResult<T>(T? Value, string? ErrorKey = null, object[]? ErrorArgs = null)
    where T : class;
