using Humans.AuditLog.Contracts;
using Humans.Camps.Contracts;
using Microsoft.Extensions.Localization;
using Humans.Events.Services.Dtos;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Calendar.Contracts;
using Humans.Settings.Contracts;
using Humans.Events.Domain;
using NodaTime;
using Humans.Events.Contracts;
using Humans.Events.Data;
using Humans.Users.Contracts;

namespace Humans.Events.Services;

internal sealed class EventService(
    IEventRepository repo,
    ISettingsService settingsService,
    IUserService userService,
    ICampService camps,
    IAuditLogService audit,
    IEmailService emailService,
    EventsEmails emailMessages,
    IClock clock,
    ILogger<EventService> logger,
    IStringLocalizer<EventsResource> localizer)
    : IEventManagement, ICalendarFeedContributor
{
    private readonly EventGuideAuthoring authoring = new(repo, settingsService, userService, emailService, emailMessages, clock, logger, localizer);
    private readonly EventGuideExport guideExport = new(repo, settingsService, userService, camps, audit);
    private readonly EventGuideConfiguration guideConfiguration = new(repo, settingsService, clock, logger);
    private readonly EventPersonalData personalData = new(repo, settingsService, clock, logger, localizer, userService);

    public Task<EventGuideSettingsView?> GetGuideSettingsAsync(CancellationToken ct = default) =>
        guideConfiguration.GetGuideSettingsAsync(ct);

    public Task<IReadOnlyList<EventSettingsInfo>> GetEventSettingsOptionsAsync(CancellationToken ct = default) =>
        guideConfiguration.GetEventSettingsOptionsAsync(ct);

    public Task<EventSettingsInfo?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default) =>
        guideConfiguration.GetEventSettingsByIdAsync(id, ct);

    public Task<bool> SaveGuideSettingsAsync(
        Guid? existingId, Guid eventSettingsId,
        LocalDateTime submissionOpenAt, LocalDateTime submissionCloseAt, LocalDateTime guidePublishAt,
        int maxPrintSlots, CancellationToken ct = default) =>
        guideConfiguration.SaveGuideSettingsAsync(
            existingId, eventSettingsId, submissionOpenAt, submissionCloseAt, guidePublishAt, maxPrintSlots, ct);

    public Task<IReadOnlyList<EventCategoryView>> GetActiveCategoriesAsync(CancellationToken ct = default) =>
        guideConfiguration.GetActiveCategoriesAsync(ct);

    public Task<IReadOnlyList<EventCategoryManageInfo>> GetAllCategoriesAsync(CancellationToken ct = default) =>
        guideConfiguration.GetAllCategoriesAsync(ct);

    public Task<EventCategoryView?> GetCategoryAsync(Guid id, CancellationToken ct = default) =>
        guideConfiguration.GetCategoryAsync(id, ct);

    public Task<bool> CategorySlugExistsAsync(string slug, Guid? excludeId = null, CancellationToken ct = default) =>
        guideConfiguration.CategorySlugExistsAsync(slug, excludeId, ct);

    public Task<int> GetNextCategoryOrderAsync(CancellationToken ct = default) =>
        guideConfiguration.GetNextCategoryOrderAsync(ct);

    public Task CreateCategoryAsync(EventCategory category, CancellationToken ct = default) =>
        guideConfiguration.CreateCategoryAsync(category, ct);

    public Task UpdateCategoryAsync(EventCategory category, CancellationToken ct = default) =>
        guideConfiguration.UpdateCategoryAsync(category, ct);

    public Task<(bool deleted, int linkedCount)> DeleteCategoryAsync(Guid id, CancellationToken ct = default) =>
        guideConfiguration.DeleteCategoryAsync(id, ct);

    public Task MoveCategoryAsync(Guid id, int direction, CancellationToken ct = default) =>
        guideConfiguration.MoveCategoryAsync(id, direction, ct);

    public Task<IReadOnlyList<EventVenueView>> GetActiveVenuesAsync(CancellationToken ct = default) =>
        guideConfiguration.GetActiveVenuesAsync(ct);

    public Task<IReadOnlyList<EventVenueManageInfo>> GetAllVenuesAsync(CancellationToken ct = default) =>
        guideConfiguration.GetAllVenuesAsync(ct);

    public Task<EventVenueView?> GetVenueAsync(Guid id, CancellationToken ct = default) =>
        guideConfiguration.GetVenueAsync(id, ct);

    public Task<int> GetNextVenueOrderAsync(CancellationToken ct = default) =>
        guideConfiguration.GetNextVenueOrderAsync(ct);

    public Task CreateVenueAsync(EventVenue venue, CancellationToken ct = default) =>
        guideConfiguration.CreateVenueAsync(venue, ct);

    public Task UpdateVenueAsync(EventVenue venue, CancellationToken ct = default) =>
        guideConfiguration.UpdateVenueAsync(venue, ct);

    public Task<(bool deleted, int linkedCount)> DeleteVenueAsync(Guid id, CancellationToken ct = default) =>
        guideConfiguration.DeleteVenueAsync(id, ct);

    public Task MoveVenueAsync(Guid id, int direction, CancellationToken ct = default) =>
        guideConfiguration.MoveVenueAsync(id, direction, ct);

    public async Task<IReadOnlyList<EventInfo>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default)
    {
        var guideEventId = await GetGuideEventIdAsync(ct);
        var events = await repo.GetUserSubmissionsAsync(userId, ct);
        return events.Where(e => e.IsInEvent(guideEventId)).Select(EventInfo.FromEvent).ToList();
    }

    public async Task<EventInfo?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default)
    {
        var guideEvent = await repo.GetUserEventAsync(eventId, userId, ct);
        return guideEvent is null ? null : EventInfo.FromEvent(guideEvent);
    }

    public async Task<IReadOnlyList<EventInfo>> GetCampSubmissionsAsync(Guid campId, CancellationToken ct = default)
    {
        var guideEventId = await GetGuideEventIdAsync(ct);
        var events = await repo.GetCampSubmissionsAsync(campId, ct);
        return events.Where(e => e.IsInEvent(guideEventId)).Select(EventInfo.FromEvent).ToList();
    }

    public async Task<EventInfo?> GetCampEventAsync(Guid eventId, Guid campId, CancellationToken ct = default)
    {
        var guideEvent = await repo.GetCampEventAsync(eventId, campId, ct);
        return guideEvent is null ? null : EventInfo.FromEvent(guideEvent);
    }

    public async Task<CampSubmissionsSummary> GetCampSubmissionsSummaryAsync(Guid campId, CancellationToken ct = default)
    {
        var events = await GetCampSubmissionsAsync(campId, ct);
        return new CampSubmissionsSummary(
            SubmittedCount: events.Count,
            ApprovedCount: events.Count(e => e.Status == EventStatus.Approved),
            PendingCount: events.Count(e => e.Status == EventStatus.Pending),
            Events: events);
    }

    public Task<EventSaveResult> SubmitEventAsync(Event guideEvent, string? lifecycleActionUrl = null, CancellationToken ct = default) =>
        authoring.SubmitEventAsync(guideEvent, lifecycleActionUrl, ct);

    public Task<EventSaveResult> UpdateAndResubmitAsync(Guid eventId, EventEdit edit, CancellationToken ct = default) =>
        authoring.UpdateAndResubmitAsync(eventId, edit, ct);

    public Task WithdrawEventAsync(Guid eventId, CancellationToken ct = default) =>
        authoring.WithdrawEventAsync(eventId, ct);

    public Task<bool> ModeratorWithdrawAsync(Guid eventId, Guid actorUserId, CancellationToken ct = default) =>
        authoring.ModeratorWithdrawAsync(eventId, actorUserId, ct);

    public Task AdminUpdateAsync(Guid eventId, EventEdit edit, Guid actorUserId, string? note, CancellationToken ct = default) =>
        authoring.AdminUpdateAsync(eventId, edit, actorUserId, note, ct);

    public Task<BulkImportResult> BulkImportAsync(
        Guid campId, Guid submitterUserId, IReadOnlyList<BulkCsvRow> rows,
        LocalDate gateOpeningDate, int eventEndOffset, DateTimeZone timeZone,
        CancellationToken ct = default) =>
        authoring.BulkImportAsync(campId, submitterUserId, rows, gateOpeningDate, eventEndOffset, timeZone, ct);

    private Task<Guid?> GetGuideEventIdAsync(CancellationToken ct) =>
        personalData.GetGuideEventIdAsync(ct);

    public async Task<byte[]> BuildBulkUploadTemplateAsync(Guid campId, string campName, CancellationToken ct = default)
    {
        var guideSettings = await GetGuideSettingsAsync(ct);
        var eventSettings = guideSettings != null
            ? await GetEventSettingsByIdAsync(guideSettings.EventSettingsId, ct)
            : null;

        var campEvents = await GetCampSubmissionsAsync(campId, ct);
        var categories = await GetActiveCategoriesAsync(ct);

        DateTimeZone? tz = eventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
            : null;
        LocalDate? gateDate = eventSettings?.GateOpeningDate;
        return EventBulkUploadTemplateBuilder.Build(campName, campEvents, categories, tz, gateDate, clock);
    }

    public async Task<IReadOnlyList<ApprovedEventView>> GetApprovedEventsAsync(
        Guid? campId, Guid? venueId, Guid? categoryId, string? q,
        IReadOnlyList<string> excludedSlugs, CancellationToken ct = default)
    {
        var guideEventId = await GetGuideEventIdAsync(ct);
        var events = await repo.GetApprovedEventsAsync(campId, venueId, categoryId, q, excludedSlugs, ct);
        return events.Where(e => e.IsInEvent(guideEventId)).Select(ToApprovedEventView).ToList();
    }

    public async Task<ApprovedEventView?> GetApprovedEventByIdAsync(Guid id, CancellationToken ct = default)
    {
        var ev = await repo.GetApprovedEventByIdAsync(id, ct);
        return ev is not null && ev.IsInEvent(await GetGuideEventIdAsync(ct)) ? ToApprovedEventView(ev) : null;
    }

    // Search is served from the cache in CachingEventService and must never hit the database; reaching this means a
    // DI registration mistake.
    public Task<IReadOnlyList<EventSearchHit>> SearchAsync(
        string query, int max, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Event search runs against the cached approved-event snapshot in " +
            "CachingEventService. If this is being called on the inner Service it " +
            "indicates a DI registration mistake — IEventService must resolve to " +
            "the caching decorator.");

    public Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default) =>
        personalData.GetFavouriteEventIdsAsync(userId, ct);

    public Task<IReadOnlyList<EventFavouriteInfo>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default) =>
        personalData.GetFavouritesWithEventsAsync(userId, ct);

    public Task<AddFavouriteOutcome> AddFavouriteAsync(Guid userId, Guid eventId, int? dayOffset, CancellationToken ct = default) =>
        personalData.AddFavouriteAsync(userId, eventId, dayOffset, ct);

    public Task<bool> RemoveFavouriteAsync(Guid userId, Guid eventId, int? dayOffset, CancellationToken ct = default) =>
        personalData.RemoveFavouriteAsync(userId, eventId, dayOffset, ct);

    public Task<List<string>> GetExcludedCategorySlugsAsync(Guid userId, CancellationToken ct = default) =>
        personalData.GetExcludedCategorySlugsAsync(userId, ct);

    public Task SavePreferenceAsync(Guid userId, List<string> slugs, CancellationToken ct = default) =>
        personalData.SavePreferenceAsync(userId, slugs, ct);

    public async Task<Dictionary<EventStatus, int>> GetEventStatusCountsAsync(CancellationToken ct = default)
        => await repo.GetModerationStatusCountsAsync(await GetGuideEventIdAsync(ct), ct);

    public async Task<IReadOnlyList<EventInfo>> GetEventsByStatusAsync(EventStatus status, CancellationToken ct = default)
    {
        var guideEventId = await GetGuideEventIdAsync(ct);
        var events = await repo.GetEventsByStatusAsync(status, ct);
        return events.Where(e => e.IsInEvent(guideEventId)).Select(EventInfo.FromEvent).ToList();
    }

    public async Task<EventInfo?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default)
    {
        var guideEvent = await repo.GetEventWithDetailsAsync(eventId, ct);
        return guideEvent is null ? null : EventInfo.FromEvent(guideEvent);
    }

    public async Task<IReadOnlyList<CampEventOverlap>> GetCampEventsForOverlapAsync(CancellationToken ct = default)
        => await repo.GetActiveCampEventsAsync(await GetGuideEventIdAsync(ct), ct);

    public Task<ModerationOutcome> ApplyModerationAsync(
        Guid eventId, Guid actorUserId, EventModerationActionType actionType, string? reason,
        string? submitterEditUrl = null, CancellationToken ct = default) =>
        authoring.ApplyModerationAsync(eventId, actorUserId, actionType, reason, submitterEditUrl, ct);

    public async Task<IReadOnlyList<EventInfo>> GetAllEventsForDashboardAsync(CancellationToken ct = default)
    {
        var guideEventId = await GetGuideEventIdAsync(ct);
        var events = await repo.GetAllEventsForDashboardAsync(ct);
        return events.Where(e => e.IsInEvent(guideEventId)).Select(EventInfo.FromEvent).ToList();
    }

    public Task<byte[]> GetApprovedEventsForExportAsync(Guid actorUserId, CancellationToken ct = default) =>
        guideExport.GetApprovedEventsForExportAsync(actorUserId, ct);

    private static ApprovedEventView ToApprovedEventView(Event e) => new(
        Id: e.Id,
        CampId: e.CampId,
        GuideSharedVenueId: e.GuideSharedVenueId,
        SubmitterUserId: e.SubmitterUserId,
        CategoryId: e.CategoryId,
        CategorySlug: e.Category.Slug,
        CategoryName: e.Category.Name,
        CategoryIsSensitive: e.Category.IsSensitive,
        VenueName: e.EventVenue?.Name,
        Title: e.Title,
        Description: e.Description,
        LocationNote: e.LocationNote,
        Host: e.Host,
        StartAt: e.StartAt,
        DurationMinutes: e.DurationMinutes,
        IsRecurring: e.IsRecurring,
        RecurrenceDays: e.RecurrenceDays,
        PriorityRank: e.PriorityRank,
        SubmittedAt: e.SubmittedAt,
        LastUpdatedAt: e.LastUpdatedAt);

    // Nothing public to contribute to the community calendar yet.
    public Task<IReadOnlyList<CalendarFeedItem>> GetPublicItemsForWindowAsync(Instant from, Instant to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CalendarFeedItem>>([]);

    public Task<IReadOnlyList<CalendarFeedItem>> GetCalendarItemsForUserAsync(Guid userId, CancellationToken ct) =>
        personalData.GetCalendarItemsForUserAsync(userId, ct);

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        personalData.ContributeForUserAsync(userId, ct);

    internal const string Events = EventPersonalData.Events;

    // Static and internal: CachingEventService returns it from an uninitialized instance.
    internal static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Events] =
                "Partially retained: favourites and the category-exclusion preference are deleted " +
                "outright. An event the person submitted stays in the guide as the programme " +
                "record of what ran — GDPR Art. 17(3)(b): it is a listing other people " +
                "favourited, scheduled around and moderated, and event_moderation_actions " +
                "references it under a Restrict FK, so removing it would take another human's " +
                "moderation trail with it. Their Host display name is cleared, and " +
                "SubmitterUserId (non-nullable) resolves to the account tombstone."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>Submitted events stay, with the person's <c>Host</c> cleared; see <see cref="Erasure"/> for why.</summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        await repo.DeleteFavouritesAndPreferenceForUserAsync(userId, ct);
        await repo.ClearSubmitterHostForUserAsync(userId, ct);
    }

    /// <summary>Must stay idempotent.</summary>
    public Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        repo.ReassignToUserAsync(mergedFromUserId, mergedToUserId, now, ct);
}
