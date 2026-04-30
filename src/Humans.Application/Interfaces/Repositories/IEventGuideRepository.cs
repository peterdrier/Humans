using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

public record CampEventOverlap(
    Guid Id,
    Guid? CampId,
    string Title,
    Instant StartAt,
    int DurationMinutes,
    GuideEventStatus Status);

/// <summary>
/// Data-access interface for the EventGuide section. The only file that may
/// touch the guide_* and event_categories tables after the §15 migration.
/// </summary>
public interface IEventGuideRepository
{
    // ── Settings ─────────────────────────────────────────────────────────
    Task<GuideSettings?> GetGuideSettingsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EventSettings>> GetActiveEventSettingsAsync(CancellationToken ct = default);
    Task<EventSettings?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default);
    void Add(GuideSettings settings);

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
    Task<IReadOnlyList<GuideSharedVenue>> GetActiveVenuesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GuideSharedVenue>> GetAllVenuesAsync(CancellationToken ct = default);
    Task<GuideSharedVenue?> GetVenueAsync(Guid id, CancellationToken ct = default);
    Task<GuideSharedVenue?> GetVenueWithEventsAsync(Guid id, CancellationToken ct = default);
    Task<int> GetMaxVenueOrderAsync(CancellationToken ct = default);
    Task<List<GuideSharedVenue>> GetAllVenuesOrderedForSwapAsync(CancellationToken ct = default);
    void Add(GuideSharedVenue venue);
    void Remove(GuideSharedVenue venue);

    // ── Events (submitter) ────────────────────────────────────────────────
    Task<IReadOnlyList<GuideEvent>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default);
    Task<GuideEvent?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default);
    void Add(GuideEvent guideEvent);

    // ── Events (browse / export / API) ────────────────────────────────────
    Task<IReadOnlyList<GuideEvent>> GetApprovedEventsAsync(
        Guid? campId, Guid? venueId, Guid? categoryId, string? q,
        IReadOnlyList<string> excludedSlugs, CancellationToken ct = default);
    Task<GuideEvent?> GetApprovedEventByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<GuideEvent>> GetAllEventsForDashboardAsync(CancellationToken ct = default);

    // ── Events (moderation) ───────────────────────────────────────────────
    Task<Dictionary<GuideEventStatus, int>> GetModerationStatusCountsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GuideEvent>> GetEventsByStatusAsync(GuideEventStatus status, CancellationToken ct = default);
    Task<GuideEvent?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default);
    Task<IReadOnlyList<CampEventOverlap>> GetActiveCampEventsAsync(CancellationToken ct = default);
    void Add(ModerationAction action);

    // ── Favourites ────────────────────────────────────────────────────────
    Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserEventFavourite>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default);
    Task<UserEventFavourite?> GetFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default);
    Task<bool> FavouriteExistsAsync(Guid userId, Guid eventId, CancellationToken ct = default);
    void Add(UserEventFavourite fav);
    void Remove(UserEventFavourite fav);

    // ── Preferences ───────────────────────────────────────────────────────
    Task<UserGuidePreference?> GetPreferenceAsync(Guid userId, CancellationToken ct = default);
    void Add(UserGuidePreference pref);

    // ── Persistence ───────────────────────────────────────────────────────
    Task SaveChangesAsync(CancellationToken ct = default);
}
