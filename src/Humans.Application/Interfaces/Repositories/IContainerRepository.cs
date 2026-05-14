using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

public interface IContainerRepository : IRepository
{
    Task<IReadOnlyList<Container>> GetByCampAsync(Guid campId, CancellationToken ct = default);
    Task<IReadOnlyList<Container>> GetAllAsync(CancellationToken ct = default);
    Task<Container?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Container> AddAsync(Container container, CancellationToken ct = default);
    Task<Container> UpdateAsync(Container container, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    // Placement
    Task<ContainerPlacement?> GetPlacementAsync(Guid containerId, int year, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerPlacement>> GetPlacementsByYearAsync(int year, CancellationToken ct = default);
    Task UpsertPlacementAsync(ContainerPlacement placement, CancellationToken ct = default);

    /// <summary>
    /// Verifies the container exists and upserts the placement's geometry in a
    /// single DbContext, preserving notes/image metadata on the existing row.
    /// Throws <see cref="InvalidOperationException"/> if the container is missing.
    /// </summary>
    Task<ContainerPlacement> SavePlacementGeometryAsync(
        Guid containerId, int year, string geoJson, Instant now, CancellationToken ct = default);

    Task DeletePlacementAsync(Guid containerId, int year, CancellationToken ct = default);
}
