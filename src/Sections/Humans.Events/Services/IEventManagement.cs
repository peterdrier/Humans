using Humans.Events.Services.Dtos;
using Humans.Settings.Contracts;
using Humans.Events.Domain;
using NodaTime;
using Humans.Events.Contracts;
using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;

namespace Humans.Events.Services;

internal interface IEventManagement : IApplicationService, IEventService
{
    Task<IReadOnlyList<EventSettingsInfo>> GetEventSettingsOptionsAsync(CancellationToken ct = default);
    Task<EventSettingsInfo?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default);
    /// <summary>False when the selected event edition no longer exists; dependency failures propagate.</summary>
    Task<bool> SaveGuideSettingsAsync(
        Guid? existingId, Guid eventSettingsId,
        LocalDateTime submissionOpenAt, LocalDateTime submissionCloseAt, LocalDateTime guidePublishAt,
        int maxPrintSlots, CancellationToken ct = default);

    Task<IReadOnlyList<EventCategoryView>> GetActiveCategoriesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EventCategoryManageInfo>> GetAllCategoriesAsync(CancellationToken ct = default);
    Task<EventCategoryView?> GetCategoryAsync(Guid id, CancellationToken ct = default);
    Task<bool> CategorySlugExistsAsync(string slug, Guid? excludeId = null, CancellationToken ct = default);
    Task<int> GetNextCategoryOrderAsync(CancellationToken ct = default);
    Task CreateCategoryAsync(EventCategory category, CancellationToken ct = default);
    Task UpdateCategoryAsync(EventCategory category, CancellationToken ct = default);
    /// <summary>Only when no events reference it. Returns (false, count) when linked, (false, -1) when not found.</summary>
    Task<(bool deleted, int linkedCount)> DeleteCategoryAsync(Guid id, CancellationToken ct = default);
    Task MoveCategoryAsync(Guid id, int direction, CancellationToken ct = default);

    Task<IReadOnlyList<EventVenueView>> GetActiveVenuesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EventVenueManageInfo>> GetAllVenuesAsync(CancellationToken ct = default);
    Task<EventVenueView?> GetVenueAsync(Guid id, CancellationToken ct = default);
    Task<int> GetNextVenueOrderAsync(CancellationToken ct = default);
    Task CreateVenueAsync(EventVenue venue, CancellationToken ct = default);
    Task UpdateVenueAsync(EventVenue venue, CancellationToken ct = default);
    /// <summary>Only when no events reference it. Returns (false, count) when linked, (false, -1) when not found.</summary>
    Task<(bool deleted, int linkedCount)> DeleteVenueAsync(Guid id, CancellationToken ct = default);
    Task MoveVenueAsync(Guid id, int direction, CancellationToken ct = default);

    Task<IReadOnlyList<EventInfo>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default);
    Task<EventInfo?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<EventInfo>> GetCampSubmissionsAsync(Guid campId, CancellationToken ct = default);
    Task<EventInfo?> GetCampEventAsync(Guid eventId, Guid campId, CancellationToken ct = default);
    Task<CampSubmissionsSummary> GetCampSubmissionsSummaryAsync(Guid campId, CancellationToken ct = default);
    // Also emails the submitter a confirmation; a null lifecycleActionUrl opts out (bulk import).
    Task SubmitEventAsync(Event guideEvent, string? lifecycleActionUrl = null, CancellationToken ct = default);
    /// <summary>Re-queues for moderation; the event's own <c>CampId</c> picks the field set.</summary>
    Task UpdateAndResubmitAsync(Guid eventId, EventEdit edit, CancellationToken ct = default);
    Task WithdrawEventAsync(Guid eventId, CancellationToken ct = default);

    /// <summary>
    /// Allowed whenever the submitter could withdraw; otherwise returns false and writes nothing. Appends a
    /// <see cref="EventModerationActionType.Withdrawn"/> row naming the moderator.
    /// </summary>
    Task<bool> ModeratorWithdrawAsync(Guid eventId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Leaves <see cref="Event.Status"/> untouched (an Approved event stays published), unlike
    /// <see cref="UpdateAndResubmitAsync"/>. Appends an <see cref="EventModerationActionType.Edited"/> row with
    /// <paramref name="note"/> as its reason.
    /// </summary>
    Task AdminUpdateAsync(Guid eventId, EventEdit edit, Guid actorUserId, string? note, CancellationToken ct = default);

    /// <summary>
    /// All or nothing: creates new events (empty Id) and re-queues edited ones only when every row passes; otherwise
    /// returns per-row errors with nothing written.
    /// </summary>
    Task<BulkImportResult> BulkImportAsync(
        Guid campId, Guid submitterUserId, IReadOnlyList<BulkCsvRow> rows,
        LocalDate gateOpeningDate, int eventEndOffset, DateTimeZone timeZone,
        CancellationToken ct = default);

    /// <summary>
    /// Existing non-withdrawn submissions, or one example row when there are none, plus the instructional banner.
    /// Events cannot see camp identity, so the caller resolves <paramref name="campName"/>.
    /// </summary>
    Task<byte[]> BuildBulkUploadTemplateAsync(Guid campId, string campName, CancellationToken ct = default);

    Task<IReadOnlyList<EventFavouriteInfo>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default);
    // Null dayOffset means the whole event.
    Task<bool> AddFavouriteAsync(Guid userId, Guid eventId, int? dayOffset, CancellationToken ct = default);
    Task<bool> RemoveFavouriteAsync(Guid userId, Guid eventId, int? dayOffset, CancellationToken ct = default);

    Task<List<string>> GetExcludedCategorySlugsAsync(Guid userId, CancellationToken ct = default);
    Task SavePreferenceAsync(Guid userId, List<string> slugs, CancellationToken ct = default);

    Task<Dictionary<EventStatus, int>> GetEventStatusCountsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EventInfo>> GetEventsByStatusAsync(EventStatus status, CancellationToken ct = default);
    Task<EventInfo?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default);
    Task<IReadOnlyList<CampEventOverlap>> GetCampEventsForOverlapAsync(CancellationToken ct = default);
    // Also emails the submitter the outcome; a null submitterEditUrl opts out. Refuses when a reason is missing or
    // the event is not pending.
    Task<ModerationOutcome> ApplyModerationAsync(Guid eventId, Guid actorUserId, EventModerationActionType actionType, string? reason, string? submitterEditUrl = null, CancellationToken ct = default);

    // On the interface so the caching decorator, which carries these (erasure edits cached rows), reaches the inner
    // service.
    Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct);
    Task EraseForUserAsync(Guid userId, CancellationToken ct);

    Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct);

    Task<IReadOnlyList<EventInfo>> GetAllEventsForDashboardAsync(CancellationToken ct = default);
    /// <summary>Audits the expanded row count and recipient.</summary>
    Task<byte[]> GetApprovedEventsForExportAsync(Guid actorUserId, CancellationToken ct = default);
}
