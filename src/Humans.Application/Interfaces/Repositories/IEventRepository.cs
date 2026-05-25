using Humans.Application.DTOs.Events;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Attributes;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Data-access interface for the Events section. Owns the event_* tables.
/// Implementation uses <c>IDbContextFactory&lt;HumansDbContext&gt;</c> so the
/// repository can be registered Singleton while <c>HumansDbContext</c> stays
/// Scoped — every method opens its own short-lived context.
/// <para>
/// EventSettings is owned by the Shifts section; <see cref="EventService"/>
/// stitches in EventSettings reads via <c>IShiftManagementService</c>. This
/// repository never touches <c>event_settings</c> directly
/// (memory/architecture/no-cross-section-ef-joins.md).
/// </para>
/// </summary>
[Section("Events")]
public interface IEventRepository : IRepository
{
    // ── Settings ─────────────────────────────────────────────────────────
    Task<EventGuideSettings?> GetGuideSettingsAsync(CancellationToken ct = default);

    Task UpsertGuideSettingsAsync(EventGuideSettings settings, CancellationToken ct = default);

    // ── Categories ────────────────────────────────────────────────────────
    Task<IReadOnlyList<EventCategory>> GetActiveCategoriesAsync(CancellationToken ct = default);
    /// <summary>
    /// All categories — active + inactive — with the <c>.Events</c> include.
    /// Used by the admin list view AND <c>CachingEventService</c> snapshot
    /// (the latter pays the small Include cost; ~30 categories at this scale).
    /// </summary>
    Task<IReadOnlyList<EventCategory>> GetAllCategoriesAsync(CancellationToken ct = default);
    Task<EventCategory?> GetCategoryAsync(Guid id, CancellationToken ct = default);
    Task<bool> CategorySlugExistsAsync(string slug, Guid? excludeId, CancellationToken ct = default);
    Task<int> GetMaxCategoryOrderAsync(CancellationToken ct = default);
    Task AddCategoryAsync(EventCategory category, CancellationToken ct = default);
    Task SaveCategoryAsync(EventCategory category, CancellationToken ct = default);
    /// <summary>Deletes when no events reference the category. Returns (deleted, linkedCount); linkedCount=-1 means not found.</summary>
    Task<(bool deleted, int linkedCount)> DeleteCategoryAsync(Guid id, CancellationToken ct = default);
    Task SwapCategoryOrderAsync(Guid id, int direction, CancellationToken ct = default);

    // ── Venues ────────────────────────────────────────────────────────────
    Task<IReadOnlyList<EventVenue>> GetActiveVenuesAsync(CancellationToken ct = default);
    /// <summary>
    /// All venues — active + inactive — with the <c>.Events</c> include.
    /// Used by the admin list view AND <c>CachingEventService</c> snapshot
    /// (the latter pays the small Include cost; ~30 venues at this scale).
    /// </summary>
    Task<IReadOnlyList<EventVenue>> GetAllVenuesAsync(CancellationToken ct = default);
    Task<EventVenue?> GetVenueAsync(Guid id, CancellationToken ct = default);
    Task<int> GetMaxVenueOrderAsync(CancellationToken ct = default);
    Task AddVenueAsync(EventVenue venue, CancellationToken ct = default);
    Task SaveVenueAsync(EventVenue venue, CancellationToken ct = default);
    /// <summary>Deletes when no events reference the venue. Returns (deleted, linkedCount); linkedCount=-1 means not found.</summary>
    Task<(bool deleted, int linkedCount)> DeleteVenueAsync(Guid id, CancellationToken ct = default);
    Task SwapVenueOrderAsync(Guid id, int direction, CancellationToken ct = default);

    // ── Events (submitter) ────────────────────────────────────────────────
    Task<IReadOnlyList<Event>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default);
    Task<Event?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<Event>> GetCampSubmissionsAsync(Guid campId, CancellationToken ct = default);
    Task<Event?> GetCampEventAsync(Guid eventId, Guid campId, CancellationToken ct = default);
    Task AddEventAsync(Event guideEvent, CancellationToken ct = default);
    Task SaveEventAsync(Event guideEvent, CancellationToken ct = default);

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
    /// <summary>Persists the (already-transitioned) event + appends the moderation action in one transaction.</summary>
    Task SaveEventAndModerationActionAsync(Event guideEvent, EventModerationAction action, CancellationToken ct = default);

    // ── Favourites ────────────────────────────────────────────────────────
    Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<EventFavourite>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default);
    Task<bool> FavouriteExistsAsync(Guid userId, Guid eventId, CancellationToken ct = default);
    /// <summary>Adds a favourite if absent, removes it if present. Returns whether the favourite now exists.</summary>
    Task<bool> ToggleFavouriteAsync(Guid userId, Guid eventId, EventFavourite newFavourite, CancellationToken ct = default);
    /// <summary>Adds only when absent. Returns false if a favourite already existed.</summary>
    Task<bool> AddFavouriteIfAbsentAsync(EventFavourite favourite, CancellationToken ct = default);
    /// <summary>Removes if present. Returns false if no favourite existed.</summary>
    Task<bool> RemoveFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default);

    // ── Preferences ───────────────────────────────────────────────────────
    Task<EventPreference?> GetPreferenceAsync(Guid userId, CancellationToken ct = default);
    Task UpsertPreferenceAsync(Guid userId, string excludedCategorySlugsJson, NodaTime.Instant updatedAt, CancellationToken ct = default);

    // ── GDPR contributor ──────────────────────────────────────────────────
    /// <summary>All favourite rows for the user, regardless of underlying event status.</summary>
    Task<IReadOnlyList<EventFavourite>> GetFavouritesForContributorAsync(Guid userId, CancellationToken ct = default);
}
