using Profiles.Application.DTOs;

namespace Profiles.Application.Interfaces;

/// <summary>
/// Service for managing volunteer history entries.
/// </summary>
public interface IVolunteerHistoryService
{
    /// <summary>
    /// Gets all volunteer history entries for a profile, ordered by date descending.
    /// </summary>
    /// <param name="profileId">The profile to get entries for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Volunteer history entries ordered by date descending (newest first).</returns>
    Task<IReadOnlyList<VolunteerHistoryEntryDto>> GetAllAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves volunteer history entries for a profile (upsert/delete).
    /// </summary>
    /// <param name="profileId">The profile to save entries for.</param>
    /// <param name="entries">The entries to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(
        Guid profileId,
        IReadOnlyList<VolunteerHistoryEntryEditDto> entries,
        CancellationToken cancellationToken = default);
}
