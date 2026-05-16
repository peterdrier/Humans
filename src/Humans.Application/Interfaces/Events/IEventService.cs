using Humans.Application.Architecture;
using Humans.Application.DTOs.Events;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Events;

/// <summary>
/// Service for the Events section (camp-event guide).
/// </summary>
/// <remarks>
/// Surface-budget recent history (newest first):
/// <list type="bullet">
///   <item>2026-05-14 — initial budget pinned at 46 after Stage 3 cross-section strip and Stage 5 IUserDataContributor add-on (section-align Events, issue #539).</item>
/// </list>
/// </remarks>
[SurfaceBudget(46)]
public interface IEventService : IApplicationService
{
    // ── Settings ─────────────────────────────────────────────────────────
    Task<EventGuideSettings?> GetGuideSettingsAsync(CancellationToken ct = default);
    Task<bool> IsSubmissionOpenAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EventSettings>> GetEventSettingsOptionsAsync(CancellationToken ct = default);
    Task<EventSettings?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default);
    Task SaveGuideSettingsAsync(
        Guid? existingId, Guid eventSettingsId,
        LocalDateTime submissionOpenAt, LocalDateTime submissionCloseAt, LocalDateTime guidePublishAt,
        int maxPrintSlots, CancellationToken ct = default);

    // ── Categories ────────────────────────────────────────────────────────
    Task<IReadOnlyList<EventCategory>> GetActiveCategoriesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EventCategory>> GetAllCategoriesAsync(CancellationToken ct = default);
    Task<EventCategory?> GetCategoryAsync(Guid id, CancellationToken ct = default);
    Task<bool> CategorySlugExistsAsync(string slug, Guid? excludeId = null, CancellationToken ct = default);
    Task<int> GetNextCategoryOrderAsync(CancellationToken ct = default);
    Task CreateCategoryAsync(EventCategory category, CancellationToken ct = default);
    Task UpdateCategoryAsync(EventCategory category, CancellationToken ct = default);
    /// <summary>
    /// Deletes a category when no guide events reference it.
    /// </summary>
    /// <returns>null if not found; (false, count) if has linked events; (true, 0) on success.</returns>
    Task<(bool deleted, int linkedCount)> DeleteCategoryAsync(Guid id, CancellationToken ct = default);
    Task MoveCategoryAsync(Guid id, int direction, CancellationToken ct = default);

    // ── Venues ────────────────────────────────────────────────────────────
    Task<IReadOnlyList<EventVenue>> GetActiveVenuesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EventVenue>> GetAllVenuesAsync(CancellationToken ct = default);
    Task<EventVenue?> GetVenueAsync(Guid id, CancellationToken ct = default);
    Task<int> GetNextVenueOrderAsync(CancellationToken ct = default);
    Task CreateVenueAsync(EventVenue venue, CancellationToken ct = default);
    Task UpdateVenueAsync(EventVenue venue, CancellationToken ct = default);
    /// <summary>
    /// Deletes a shared venue when no guide events reference it.
    /// </summary>
    /// <returns>(false, count) if has linked events; (true, 0) on success.</returns>
    Task<(bool deleted, int linkedCount)> DeleteVenueAsync(Guid id, CancellationToken ct = default);
    Task MoveVenueAsync(Guid id, int direction, CancellationToken ct = default);

    // ── Submissions ───────────────────────────────────────────────────────
    Task<IReadOnlyList<Event>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default);
    Task<Event?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<Event>> GetCampSubmissionsAsync(Guid campId, CancellationToken ct = default);
    Task<Event?> GetCampEventAsync(Guid eventId, Guid campId, CancellationToken ct = default);
    Task SubmitEventAsync(Event guideEvent, CancellationToken ct = default);
    Task UpdateAndResubmitAsync(Event guideEvent, CancellationToken ct = default);
    Task WithdrawEventAsync(Event guideEvent, CancellationToken ct = default);

    // ── Browse / API ──────────────────────────────────────────────────────
    Task<IReadOnlyList<Event>> GetApprovedEventsAsync(
        Guid? campId, Guid? venueId, Guid? categoryId, string? q,
        IReadOnlyList<string> excludedSlugs, CancellationToken ct = default);
    Task<Event?> GetApprovedEventByIdAsync(Guid id, CancellationToken ct = default);

    // ── Favourites ────────────────────────────────────────────────────────
    Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<EventFavourite>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default);
    Task ToggleFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default);
    Task<bool> AddFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default);
    Task<bool> RemoveFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default);

    // ── Preferences ───────────────────────────────────────────────────────
    Task<List<string>> GetExcludedCategorySlugsAsync(Guid userId, CancellationToken ct = default);
    Task<EventPreference?> GetPreferenceAsync(Guid userId, CancellationToken ct = default);
    Task SavePreferenceAsync(Guid userId, List<string> slugs, CancellationToken ct = default);

    // ── Moderation ────────────────────────────────────────────────────────
    Task<Dictionary<EventStatus, int>> GetEventStatusCountsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Event>> GetEventsByStatusAsync(EventStatus status, CancellationToken ct = default);
    Task<Event?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default);
    Task<IReadOnlyList<CampEventOverlap>> GetCampEventsForOverlapAsync(CancellationToken ct = default);
    Task ApplyModerationAsync(Guid eventId, Guid actorUserId, EventModerationActionType actionType, string? reason, CancellationToken ct = default);

    // ── Dashboard / Export ────────────────────────────────────────────────
    Task<IReadOnlyList<Event>> GetAllEventsForDashboardAsync(CancellationToken ct = default);
    Task<(IReadOnlyList<Event> Events, EventGuideSettings? Settings)> GetApprovedEventsForExportAsync(CancellationToken ct = default);
}
