using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.EventGuide;

public interface IEventGuideService
{
    // ── Settings ─────────────────────────────────────────────────────────
    Task<GuideSettings?> GetGuideSettingsAsync(CancellationToken ct = default);
    Task<bool> IsSubmissionOpenAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EventSettings>> GetEventSettingsOptionsAsync(CancellationToken ct = default);
    Task<EventSettings?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default);
    Task SaveGuideSettingsAsync(
        Guid? existingId, Guid eventSettingsId,
        DateTime submissionOpenAt, DateTime submissionCloseAt, DateTime guidePublishAt,
        int maxPrintSlots, CancellationToken ct = default);

    // ── Categories ────────────────────────────────────────────────────────
    Task<IReadOnlyList<EventCategory>> GetActiveCategoriesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EventCategory>> GetAllCategoriesAsync(CancellationToken ct = default);
    Task<EventCategory?> GetCategoryAsync(Guid id, CancellationToken ct = default);
    Task<bool> CategorySlugExistsAsync(string slug, Guid? excludeId = null, CancellationToken ct = default);
    Task<int> GetNextCategoryOrderAsync(CancellationToken ct = default);
    Task CreateCategoryAsync(EventCategory category, CancellationToken ct = default);
    Task UpdateCategoryAsync(EventCategory category, CancellationToken ct = default);
    /// <returns>null if not found; (false, count) if has linked events; (true, 0) on success.</returns>
    Task<(bool deleted, int linkedCount)> DeleteCategoryAsync(Guid id, CancellationToken ct = default);
    Task MoveCategoryAsync(Guid id, int direction, CancellationToken ct = default);

    // ── Venues ────────────────────────────────────────────────────────────
    Task<IReadOnlyList<GuideSharedVenue>> GetActiveVenuesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GuideSharedVenue>> GetAllVenuesAsync(CancellationToken ct = default);
    Task<GuideSharedVenue?> GetVenueAsync(Guid id, CancellationToken ct = default);
    Task<int> GetNextVenueOrderAsync(CancellationToken ct = default);
    Task CreateVenueAsync(GuideSharedVenue venue, CancellationToken ct = default);
    Task UpdateVenueAsync(GuideSharedVenue venue, CancellationToken ct = default);
    /// <returns>(false, count) if has linked events; (true, 0) on success.</returns>
    Task<(bool deleted, int linkedCount)> DeleteVenueAsync(Guid id, CancellationToken ct = default);
    Task MoveVenueAsync(Guid id, int direction, CancellationToken ct = default);

    // ── Submissions ───────────────────────────────────────────────────────
    Task<IReadOnlyList<GuideEvent>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default);
    Task<GuideEvent?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default);
    Task SubmitEventAsync(GuideEvent guideEvent, CancellationToken ct = default);
    Task UpdateAndResubmitAsync(GuideEvent guideEvent, CancellationToken ct = default);
    Task WithdrawEventAsync(GuideEvent guideEvent, CancellationToken ct = default);

    // ── Browse / API ──────────────────────────────────────────────────────
    Task<IReadOnlyList<GuideEvent>> GetApprovedEventsAsync(
        Guid? campId, Guid? venueId, Guid? categoryId, string? q,
        IReadOnlyList<string> excludedSlugs, CancellationToken ct = default);
    Task<GuideEvent?> GetApprovedEventByIdAsync(Guid id, CancellationToken ct = default);

    // ── Favourites ────────────────────────────────────────────────────────
    Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserEventFavourite>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default);
    Task ToggleFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default);
    Task<bool> AddFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default);
    Task<bool> RemoveFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default);

    // ── Preferences ───────────────────────────────────────────────────────
    Task<List<string>> GetExcludedCategorySlugsAsync(Guid userId, CancellationToken ct = default);
    Task<UserGuidePreference?> GetPreferenceAsync(Guid userId, CancellationToken ct = default);
    Task SavePreferenceAsync(Guid userId, List<string> slugs, CancellationToken ct = default);

    // ── Moderation ────────────────────────────────────────────────────────
    Task<Dictionary<GuideEventStatus, int>> GetEventStatusCountsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GuideEvent>> GetEventsByStatusAsync(GuideEventStatus status, CancellationToken ct = default);
    Task<GuideEvent?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default);
    Task<IReadOnlyList<CampEventOverlap>> GetCampEventsForOverlapAsync(CancellationToken ct = default);
    Task ApplyModerationAsync(Guid eventId, Guid actorUserId, ModerationActionType actionType, string? reason, CancellationToken ct = default);

    // ── Dashboard / Export ────────────────────────────────────────────────
    Task<IReadOnlyList<GuideEvent>> GetAllEventsForDashboardAsync(CancellationToken ct = default);
    Task<(IReadOnlyList<GuideEvent> Events, GuideSettings? Settings)> GetApprovedEventsForExportAsync(CancellationToken ct = default);
}
