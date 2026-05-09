using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Repositories;

public interface IContainerRepository
{
    Task<IReadOnlyList<Container>> GetByCampAsync(Guid campId, int year, CancellationToken ct = default);
    Task<IReadOnlyList<Container>> GetOrgByYearAsync(int year, CancellationToken ct = default);
    Task<IReadOnlyList<Container>> GetAllByYearAsync(int year, CancellationToken ct = default);
    Task<Container?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Container> AddAsync(Container container, CancellationToken ct = default);
    Task<Container> UpdateAsync(Container container, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
