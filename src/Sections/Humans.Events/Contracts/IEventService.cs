using Humans.Base.Interfaces;

namespace Humans.Events.Contracts;

/// <summary>Approved-event reads only, served from the approved-events cache.</summary>
public interface IEventService : IApplicationService
{
    /// <summary>Minus the caller's excluded category slugs.</summary>
    Task<IReadOnlyList<ApprovedEventView>> GetApprovedEventsAsync(
        Guid? campId, Guid? venueId, Guid? categoryId, string? q,
        IReadOnlyList<string> excludedSlugs, CancellationToken ct = default);

    Task<ApprovedEventView?> GetApprovedEventByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Carries <c>TimeZoneId</c> for rendering start times in the burn's local zone.</summary>
    Task<EventGuideSettingsView?> GetGuideSettingsAsync(CancellationToken ct = default);

    Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Case-insensitive Title or Description match, scored per <see cref="EventSearchHit"/>. Capped at
    /// <paramref name="max"/>, in unspecified order: the caller ranks.
    /// </summary>
    Task<IReadOnlyList<EventSearchHit>> SearchAsync(
        string query, int max, CancellationToken ct = default);
}
