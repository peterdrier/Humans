using Humans.Base.Interfaces;

namespace Humans.Camps.Contracts;

/// <summary>
/// Implemented by a section that keeps rows keyed on camp season ids with no foreign key to Camps' tables, so
/// nothing else removes them when a camp is deleted.
/// </summary>
public interface ICampDeletionListener : IApplicationService
{
    /// <summary>
    /// Called inside the camp-delete transaction, before the seasons go; a throw rolls the delete back. Returns the
    /// rows removed, for the log.
    /// </summary>
    Task<int> CampSeasonsDeletingAsync(IReadOnlyCollection<Guid> campSeasonIds, CancellationToken cancellationToken = default);
}
