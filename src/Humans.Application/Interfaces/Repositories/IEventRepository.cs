using Humans.Application.DTOs.EventGuide;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Data-access interface for the EventGuide section. The only file that may
/// touch the guide_* and event_categories tables after the §15 migration.
/// </summary>
public interface IEventRepository : IRepository
{
    // ── Settings ─────────────────────────────────────────────────────────
    Task<EventGuideSettings?> GetGuideSettingsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EventSettings>> GetActiveEventSettingsAsync(CancellationToken ct = default);
    Task<EventSettings?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default);
    void Add(EventGuideSettings settings);

    // ── Categories ────────────────────────────────────────────────────────
    Task<IReadOnlyList<EventCategory>> GetActiveCategoriesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EventCategory>> GetAllCategoriesAsync(CancellationToken ct = default);
    Task<EventCategory?> GetCategoryAsync(Guid id, CancellationToken ct = default);
    Task<EventCategory?> GetCategoryWithEventsAsync(Guid id, CancellationToken ct = default);
    Task<bool> CategorySlugExistsAsync(string slug, Guid? excludeId, CancellationToken ct = default);
    Task<int> GetMaxCategoryOrderAsync(CancellationToken ct = default);
    Task<List<EventCategory>> GetAllCategoriesOrderedForSwapAsync(CancellationToken ct = default);
    void Add(EventCategory category);
    void Remove(EventCategory category);

    // ── Venues ────────────────────────────────────────────────────────────
    Task<IReadOnlyList<EventVenue>> GetActiveVenuesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EventVenue>> GetAllVenuesAsync(CancellationToken ct = default);
    Task<EventVenue?> GetVenueAsync(Guid id, CancellationToken ct = default);
    Task<EventVenue?> GetVenueWithEventsAsync(Guid id, CancellationToken ct = default);
    Task<int> GetMaxVenueOrderAsync(CancellationToken ct = default);
    Task<List<EventVenue>> GetAllVenuesOrderedForSwapAsync(CancellationToken ct = default);
    void Add(EventVenue venue);
    void Remove(EventVenue venue);

    // ── Events (submitter) ────────────────────────────────────────────────
    Task<IReadOnlyList<Event>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default);
    Task<Event?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<Event>> GetCampSubmissionsAsync(Guid campId, CancellationToken ct = default);
    Task<Event?> GetCampEventAsync(Guid eventId, Guid campId, CancellationToken ct = default);
    void Add(Event guideEvent);

    // ── Events (browse / export / API) ────────────────────────────────────
    Task<IReadOnlyList<Event>> GetApprovedEventsAsync(
        Guid? campId, Guid? venueId, Guid? categoryId, string? q,
        IReadOnlyList<string> excludedSlugs, CancellationToken ct = default);
    Task<Event?> GetApprovedEventByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Event>> GetAllEventsForDashboardAsync(CancellationToken ct = default);

    // ── Events (moderation) ───────────────────────────────────────────────
    Task<Dictionary<EventStatus, int>> GetModerationStatusCountsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Event>> GetEventsByStatusAsync(EventStatus status, CancellationToken ct = default);
    Task<Event?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default);
    Task<IReadOnlyList<CampEventOverlap>> GetActiveCampEventsAsync(CancellationToken ct = default);
    void Add(EventModerationAction action);

    // ── Favourites ────────────────────────────────────────────────────────
    Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<EventFavourite>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default);
    Task<EventFavourite?> GetFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default);
    Task<bool> FavouriteExistsAsync(Guid userId, Guid eventId, CancellationToken ct = default);
    void Add(EventFavourite fav);
    void Remove(EventFavourite fav);

    // ── Preferences ───────────────────────────────────────────────────────
    Task<EventPreference?> GetPreferenceAsync(Guid userId, CancellationToken ct = default);
    void Add(EventPreference pref);

    // ── Persistence ───────────────────────────────────────────────────────
    Task SaveChangesAsync(CancellationToken ct = default);
}
