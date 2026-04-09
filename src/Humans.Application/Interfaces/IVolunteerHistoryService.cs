using Humans.Application.DTOs;

namespace Humans.Application.Interfaces;

/// <summary>
/// Service for managing volunteer history entries.
/// </summary>
public interface IVolunteerHistoryService
{
    Task<IReadOnlyList<VolunteerHistoryEntryDto>> GetAllAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        Guid profileId,
        IReadOnlyList<VolunteerHistoryEntryEditDto> entries,
        CancellationToken cancellationToken = default);
}
