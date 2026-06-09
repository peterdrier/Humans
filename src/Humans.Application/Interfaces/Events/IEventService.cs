using Humans.Application.DTOs.Events;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Events;

/// <summary>
/// Service for the Events section (camp-event guide).
/// </summary>
public interface IEventService : IApplicationService, IEventServiceRead
{
    // ── Settings ─────────────────────────────────────────────────────────
    // GetGuideSettingsAsync is declared on IEventServiceRead (cross-section read surface).
    Task<bool> IsSubmissionOpenAsync(CancellationToken ct = default);
    Task<IReadOnlyList<BurnSettingsInfo>> GetEventSettingsOptionsAsync(CancellationToken ct = default);
    Task<BurnSettingsInfo?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default);
    Task SaveGuideSettingsAsync(
        Guid? existingId, Guid eventSettingsId,
        LocalDateTime submissionOpenAt, LocalDateTime submissionCloseAt, LocalDateTime guidePublishAt,
        int maxPrintSlots, CancellationToken ct = default);

    // ── Categories ────────────────────────────────────────────────────────
    Task<IReadOnlyList<EventCategoryView>> GetActiveCategoriesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EventCategoryManageInfo>> GetAllCategoriesAsync(CancellationToken ct = default);
    Task<EventCategoryView?> GetCategoryAsync(Guid id, CancellationToken ct = default);
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
    Task<IReadOnlyList<EventVenueView>> GetActiveVenuesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EventVenueManageInfo>> GetAllVenuesAsync(CancellationToken ct = default);
    Task<EventVenueView?> GetVenueAsync(Guid id, CancellationToken ct = default);
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
    Task<IReadOnlyList<EventInfo>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default);
    // Read-then-mutate-then-write round trip: result is mutated and passed to a write
    // method (WithdrawEventAsync / UpdateAndResubmitAsync), so it returns the entity.
    Task<Event?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<EventInfo>> GetCampSubmissionsAsync(Guid campId, CancellationToken ct = default);
    // Read-then-mutate-then-write round trip (see GetUserEventAsync).
    Task<Event?> GetCampEventAsync(Guid eventId, Guid campId, CancellationToken ct = default);
    Task SubmitEventAsync(Event guideEvent, CancellationToken ct = default);
    Task UpdateAndResubmitAsync(Event guideEvent, CancellationToken ct = default);
    Task WithdrawEventAsync(Event guideEvent, CancellationToken ct = default);

    /// <summary>
    /// Admin / moderator in-place edit of a mutated event. Persists the field
    /// changes and bumps <see cref="Event.LastUpdatedAt"/> but leaves
    /// <see cref="Event.Status"/> untouched (an Approved event stays Approved /
    /// published — no re-queue), appending an <see cref="EventModerationActionType.Edited"/>
    /// history record by <paramref name="actorUserId"/> with the optional
    /// <paramref name="note"/> as its reason. The authoritative "edit in a pinch"
    /// path; distinct from <see cref="UpdateAndResubmitAsync"/>, which re-queues to Pending.
    /// </summary>
    Task AdminUpdateAsync(Event guideEvent, Guid actorUserId, string? note, CancellationToken ct = default);

    /// <summary>
    /// All-or-nothing barrio bulk import: validates every parsed row first and,
    /// if all pass, creates new events (empty Id) and re-queues edited existing
    /// ones. Returns per-row errors with nothing written when validation fails.
    /// </summary>
    Task<BulkImportResult> BulkImportAsync(
        Guid campId, Guid submitterUserId, IReadOnlyList<BulkCsvRow> rows,
        LocalDate gateOpeningDate, int eventEndOffset, DateTimeZone timeZone,
        CancellationToken ct = default);

    // ── Browse / API ──────────────────────────────────────────────────────
    // GetApprovedEventsAsync is declared on IEventServiceRead (cross-section read surface).
    Task<ApprovedEventView?> GetApprovedEventByIdAsync(Guid id, CancellationToken ct = default);

    // ── Favourites ────────────────────────────────────────────────────────
    // GetFavouriteEventIdsAsync is declared on IEventServiceRead (cross-section read surface).
    Task<IReadOnlyList<EventFavouriteInfo>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default);
    Task ToggleFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default);
    Task<bool> AddFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default);
    Task<bool> RemoveFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default);

    // ── Preferences ───────────────────────────────────────────────────────
    Task<List<string>> GetExcludedCategorySlugsAsync(Guid userId, CancellationToken ct = default);
    Task<EventPreferenceInfo?> GetPreferenceAsync(Guid userId, CancellationToken ct = default);
    Task SavePreferenceAsync(Guid userId, List<string> slugs, CancellationToken ct = default);

    // ── Moderation ────────────────────────────────────────────────────────
    Task<Dictionary<EventStatus, int>> GetEventStatusCountsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EventInfo>> GetEventsByStatusAsync(EventStatus status, CancellationToken ct = default);
    // Read-then-mutate-then-write round trip (see GetUserEventAsync).
    Task<Event?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default);
    Task<IReadOnlyList<CampEventOverlap>> GetCampEventsForOverlapAsync(CancellationToken ct = default);
    Task ApplyModerationAsync(Guid eventId, Guid actorUserId, EventModerationActionType actionType, string? reason, CancellationToken ct = default);

    // ── Dashboard / Export ────────────────────────────────────────────────
    Task<IReadOnlyList<EventInfo>> GetAllEventsForDashboardAsync(CancellationToken ct = default);
    Task<ApprovedEventsExportInfo> GetApprovedEventsForExportAsync(CancellationToken ct = default);
}
