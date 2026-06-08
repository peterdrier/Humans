namespace Humans.Application.Interfaces.Events;

/// <summary>
/// Cross-section read surface for the Events section. Exposes only the
/// approved-event reads other sections need (e.g. the camp detail page's
/// events card) without granting access to submission, moderation, or
/// settings writes. Implemented by the same service that backs
/// <see cref="IEventService"/>; registered as a forward to the caching
/// singleton so reads are served from the existing T-03 cache.
/// </summary>
public interface IEventServiceRead
{
    /// <summary>
    /// Approved events, optionally filtered by camp / venue / category / free text,
    /// minus the caller's excluded category slugs. Served from the approved-only cache.
    /// </summary>
    Task<IReadOnlyList<ApprovedEventView>> GetApprovedEventsAsync(
        Guid? campId, Guid? venueId, Guid? categoryId, string? q,
        IReadOnlyList<string> excludedSlugs, CancellationToken ct = default);

    /// <summary>
    /// The guide settings singleton (or null). Carries <c>TimeZoneId</c> so
    /// consumers can render <c>Instant</c> start times in the burn's local zone.
    /// </summary>
    Task<EventGuideSettingsView?> GetGuideSettingsAsync(CancellationToken ct = default);

    /// <summary>The event ids the given user has favourited.</summary>
    Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default);
}
