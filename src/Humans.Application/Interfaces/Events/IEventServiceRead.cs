using Humans.Application.Architecture;

namespace Humans.Application.Interfaces.Events;

/// <summary>
/// Cross-section read surface for the Events section. External sections inject
/// this interface; only the cached <see cref="ApprovedEventView"/> projection,
/// no EF entities. See
/// <c>memory/architecture/section-read-write-split.md</c>.
/// </summary>
[SurfaceBudget(1)]
public interface IEventServiceRead
{
    /// <summary>
    /// Approved events for the public guide / browse path, optionally filtered
    /// by camp, venue, category, free-text query, and excluded category slugs.
    /// Returns the pre-stitched cached read model.
    /// </summary>
    Task<IReadOnlyList<ApprovedEventView>> GetApprovedEventsAsync(
        Guid? campId, Guid? venueId, Guid? categoryId, string? q,
        IReadOnlyList<string> excludedSlugs, CancellationToken ct = default);
}
