using Humans.AuditLog.Contracts;
using Humans.Base.Csv;
using Humans.Camps.Contracts;
using static Humans.Events.Helpers.EventsLookupHelpers;
using static Humans.Events.Helpers.EventsTimeHelpers;
using System.Globalization;
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
    private readonly EventPersonalData personalData = new(repo, settingsService, clock, logger, localizer, userService);

    public async Task<EventGuideSettingsView?> GetGuideSettingsAsync(CancellationToken ct = default)
    {
        var settings = await repo.GetGuideSettingsAsync(ct);
        return settings is null ? null : await ToGuideSettingsViewAsync(settings, ct);
    }

    private async Task<EventGuideSettingsView> ToGuideSettingsViewAsync(EventGuideSettings settings, CancellationToken ct)
    {
        var burn = await settingsService.GetEventSettingsByIdAsync(settings.EventSettingsId, ct);
        return new EventGuideSettingsView(
            Id: settings.Id,
            EventSettingsId: settings.EventSettingsId,
            SubmissionOpenAt: settings.SubmissionOpenAt,
            SubmissionCloseAt: settings.SubmissionCloseAt,
            GuidePublishAt: settings.GuidePublishAt,
            MaxPrintSlots: settings.MaxPrintSlots,
            TimeZoneId: burn?.TimeZoneId,
            CreatedAt: settings.CreatedAt,
            UpdatedAt: settings.UpdatedAt);
    }

    public async Task<IReadOnlyList<EventSettingsInfo>> GetEventSettingsOptionsAsync(CancellationToken ct = default)
    {
        // At most one active burn; a list keeps the admin picker forward-compatible.
        var active = await settingsService.GetActiveEventSettingsAsync(ct);
        return active is null ? [] : [active];
    }

    public Task<EventSettingsInfo?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default)
        => settingsService.GetEventSettingsByIdAsync(id, ct);

    public async Task<bool> SaveGuideSettingsAsync(
        Guid? existingId, Guid eventSettingsId,
        LocalDateTime submissionOpenAt, LocalDateTime submissionCloseAt, LocalDateTime guidePublishAt,
        int maxPrintSlots, CancellationToken ct = default)
    {
        var burn = await settingsService.GetEventSettingsByIdAsync(eventSettingsId, ct);
        if (burn is null)
        {
            logger.LogWarning("Guide settings save rejected: event settings {EventSettingsId} no longer exist", eventSettingsId);
            return false;
        }

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(burn.TimeZoneId);
        var now = clock.GetCurrentInstant();

        var settings = new EventGuideSettings
        {
            Id = existingId ?? Guid.NewGuid(),
            EventSettingsId = eventSettingsId,
            SubmissionOpenAt = ToInstant(submissionOpenAt, tz),
            SubmissionCloseAt = ToInstant(submissionCloseAt, tz),
            GuidePublishAt = ToInstant(guidePublishAt, tz),
            MaxPrintSlots = maxPrintSlots,
            CreatedAt = now,
            UpdatedAt = now
        };

        await repo.UpsertGuideSettingsAsync(settings, ct);
        return true;
    }

    public async Task<IReadOnlyList<EventCategoryView>> GetActiveCategoriesAsync(CancellationToken ct = default)
    {
        var categories = await repo.GetActiveCategoriesAsync(ct);
        return categories.Select(ToCategoryView).ToList();
    }

    public async Task<IReadOnlyList<EventCategoryManageInfo>> GetAllCategoriesAsync(CancellationToken ct = default)
    {
        var categories = await repo.GetAllCategoriesAsync(ct);
        return categories.Select(c => new EventCategoryManageInfo(
            c.Id, c.Name, c.Slug, c.IsSensitive, c.DisplayOrder, c.IsActive, c.Events.Count)).ToList();
    }

    public async Task<EventCategoryView?> GetCategoryAsync(Guid id, CancellationToken ct = default)
    {
        var category = await repo.GetCategoryAsync(id, ct);
        return category is null ? null : ToCategoryView(category);
    }

    public Task<bool> CategorySlugExistsAsync(string slug, Guid? excludeId = null, CancellationToken ct = default)
        => repo.CategorySlugExistsAsync(slug, excludeId, ct);

    public async Task<int> GetNextCategoryOrderAsync(CancellationToken ct = default)
        => await repo.GetMaxCategoryOrderAsync(ct) + 1;

    public Task CreateCategoryAsync(EventCategory category, CancellationToken ct = default)
        => repo.AddCategoryAsync(category, ct);

    public Task UpdateCategoryAsync(EventCategory category, CancellationToken ct = default)
        => repo.SaveCategoryAsync(category, ct);

    public Task<(bool deleted, int linkedCount)> DeleteCategoryAsync(Guid id, CancellationToken ct = default)
        => repo.DeleteCategoryAsync(id, ct);

    public Task MoveCategoryAsync(Guid id, int direction, CancellationToken ct = default)
        => repo.SwapCategoryOrderAsync(id, direction, ct);

    public async Task<IReadOnlyList<EventVenueView>> GetActiveVenuesAsync(CancellationToken ct = default)
    {
        var venues = await repo.GetActiveVenuesAsync(ct);
        return venues.Select(ToVenueView).ToList();
    }

    public async Task<IReadOnlyList<EventVenueManageInfo>> GetAllVenuesAsync(CancellationToken ct = default)
    {
        var venues = await repo.GetAllVenuesAsync(ct);
        return venues.Select(v => new EventVenueManageInfo(
            v.Id, v.Name, v.Description, v.LocationDescription, v.DisplayOrder, v.IsActive, v.Events.Count)).ToList();
    }

    public async Task<EventVenueView?> GetVenueAsync(Guid id, CancellationToken ct = default)
    {
        var venue = await repo.GetVenueAsync(id, ct);
        return venue is null ? null : ToVenueView(venue);
    }

    public async Task<int> GetNextVenueOrderAsync(CancellationToken ct = default)
        => await repo.GetMaxVenueOrderAsync(ct) + 1;

    public Task CreateVenueAsync(EventVenue venue, CancellationToken ct = default)
        => repo.AddVenueAsync(venue, ct);

    public Task UpdateVenueAsync(EventVenue venue, CancellationToken ct = default)
        => repo.SaveVenueAsync(venue, ct);

    public Task<(bool deleted, int linkedCount)> DeleteVenueAsync(Guid id, CancellationToken ct = default)
        => repo.DeleteVenueAsync(id, ct);

    public Task MoveVenueAsync(Guid id, int direction, CancellationToken ct = default)
        => repo.SwapVenueOrderAsync(id, direction, ct);

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

    public async Task<EventSaveResult> SubmitEventAsync(Event guideEvent, string? lifecycleActionUrl = null, CancellationToken ct = default)
    {
        if (await GetOpenSubmissionSettingsAsync(ct) is not { } settings) return new("Events_SubmissionWindowClosed");
        await EnsureValidRecurrenceDaysAsync(guideEvent.IsRecurring, guideEvent.RecurrenceDays, ct);
        if (await repo.GetCategoryAsync(guideEvent.CategoryId, ct) is not { IsActive: true })
            return new("Events_CategoryUnavailable", nameof(EventEdit.CategoryId));
        if (guideEvent.CampId is null && (guideEvent.GuideSharedVenueId is not { } venueId
            || venueId == Guid.Empty || await repo.GetVenueAsync(venueId, ct) is not { IsActive: true }))
            return new("Events_VenueUnavailable", nameof(EventEdit.VenueId));
        guideEvent.EventSettingsId = settings.EventSettingsId;
        guideEvent.Submit(clock);
        await repo.AddEventAsync(guideEvent, ct);

        // A null actionUrl opts out: bulk import would otherwise send one email per CSV row.
        if (lifecycleActionUrl is not null)
            await SendLifecycleEmailAsync(guideEvent, EventStatus.Pending, reason: null, lifecycleActionUrl);
        return new();
    }

    private async Task SendLifecycleEmailAsync(
        Event guideEvent, EventStatus newStatus, string? reason, string actionUrl)
    {
        // The change is committed: request cancellation must not skip its notification.
        // Lookup and delivery failures remain best effort.
        try
        {
            var submitter = await userService.GetUserInfoAsync(guideEvent.SubmitterUserId, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(submitter?.Email))
            {
                logger.LogWarning(
                    "Skipping lifecycle email for event {EventId}: submitter {SubmitterId} has no notification email",
                    guideEvent.Id, guideEvent.SubmitterUserId);
                return;
            }

            var submitterEmail = submitter.Email;
            var language = submitter.PreferredLanguage;

            await emailService.SendAsync(emailMessages.EventLifecycle(
                submitter.Id,
                new EventLifecycleNotification(
                    NewStatus: newStatus,
                    UserName: submitter.BurnerName,
                    EventTitle: guideEvent.Title,
                    Reason: reason,
                    ActionUrl: actionUrl,
                    Culture: language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode),
                submitterEmail));
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to send lifecycle email for event {EventId} (status {Status})",
                guideEvent.Id, newStatus);
        }
    }

    public async Task<EventSaveResult> UpdateAndResubmitAsync(Guid eventId, EventEdit edit, CancellationToken ct = default)
    {
        await EnsureValidRecurrenceDaysAsync(edit.IsRecurring, edit.RecurrenceDays, ct);
        if (await repo.GetCategoryAsync(edit.CategoryId, ct) is not { IsActive: true })
            return new("Events_CategoryUnavailable", nameof(EventEdit.CategoryId));
        var guideEvent = await LoadEventAsync(eventId, ct);
        if (guideEvent.CampId is null && (edit.VenueId is not { } venueId
            || venueId == Guid.Empty || await repo.GetVenueAsync(venueId, ct) is not { IsActive: true }))
            return new("Events_VenueUnavailable", nameof(EventEdit.VenueId));
        ApplyEdit(guideEvent, edit);
        guideEvent.Resubmit(clock);
        await repo.SaveEventAsync(guideEvent, ct);
        return new();
    }

    public async Task WithdrawEventAsync(Guid eventId, CancellationToken ct = default)
    {
        var guideEvent = await LoadEventAsync(eventId, ct);
        guideEvent.Withdraw(clock);
        await repo.SaveEventAsync(guideEvent, ct);
    }

    public async Task<bool> ModeratorWithdrawAsync(Guid eventId, Guid actorUserId, CancellationToken ct = default)
    {
        var guideEvent = await LoadEventAsync(eventId, ct);
        // A moderator may withdraw whatever the submitter could.
        if (!guideEvent.CanBeWithdrawnBySubmitter)
            return false;
        guideEvent.Withdraw(clock);

        var action = new EventModerationAction
        {
            Id = Guid.NewGuid(),
            GuideEventId = guideEvent.Id,
            ActorUserId = actorUserId,
            Action = EventModerationActionType.Withdrawn,
            CreatedAt = guideEvent.LastUpdatedAt
        };

        await repo.SaveEventAndModerationActionAsync(guideEvent, action, ct);
        return true;
    }

    public async Task AdminUpdateAsync(Guid eventId, EventEdit edit, Guid actorUserId, string? note, CancellationToken ct = default)
    {
        await EnsureValidRecurrenceDaysAsync(edit.IsRecurring, edit.RecurrenceDays, ct);
        var guideEvent = await LoadEventAsync(eventId, ct);
        ApplyEdit(guideEvent, edit);
        var now = clock.GetCurrentInstant();
        guideEvent.LastUpdatedAt = now; // Status deliberately untouched — admin edit never re-queues.

        var action = new EventModerationAction
        {
            Id = Guid.NewGuid(),
            GuideEventId = guideEvent.Id,
            ActorUserId = actorUserId,
            Action = EventModerationActionType.Edited,
            Reason = string.IsNullOrWhiteSpace(note) ? null : note,
            CreatedAt = now
        };

        await repo.SaveEventAndModerationActionAsync(guideEvent, action, ct);
    }

    /// <summary>Null when the submission window is closed or not configured.</summary>
    private async Task<EventGuideSettings?> GetOpenSubmissionSettingsAsync(CancellationToken ct)
    {
        var settings = await repo.GetGuideSettingsAsync(ct);
        if (settings is not null && settings.IsSubmissionOpenAt(clock.GetCurrentInstant())) return settings;
        logger.LogWarning("Event submission refused: the submission window is closed or not configured");
        return null;
    }

    private Task<Guid?> GetGuideEventIdAsync(CancellationToken ct) =>
        personalData.GetGuideEventIdAsync(ct);

    private async Task EnsureValidRecurrenceDaysAsync(bool isRecurring, string? recurrenceDays, CancellationToken ct)
    {
        if (!isRecurring) return;
        var settings = await repo.GetGuideSettingsAsync(ct);
        var eventSettings = settings is null ? null : await settingsService.GetEventSettingsByIdAsync(settings.EventSettingsId, ct);
        if (eventSettings is null || !EventRecurrenceDays.HasOnlyEventOffsets(recurrenceDays, eventSettings.EventEndOffset))
            throw new InvalidOperationException("Recurrence days must be day offsets inside the event calendar.");
    }

    private async Task<Event> LoadEventAsync(Guid eventId, CancellationToken ct) =>
        await repo.GetEventForModerationAsync(eventId, ct)
            ?? throw new InvalidOperationException($"Event {eventId} not found.");

    // The event's own CampId, not the caller, decides which field set applies.
    private static void ApplyEdit(Event guideEvent, EventEdit edit)
    {
        if (guideEvent.CampId.HasValue)
        {
            guideEvent.ApplyBarrioEdit(
                edit.CategoryId, edit.Title, edit.Description, edit.StartAt, edit.DurationMinutes,
                edit.LocationNote, edit.Host, edit.IsRecurring, edit.RecurrenceDays, edit.PriorityRank);
        }
        else
        {
            guideEvent.ApplyIndividualEdit(
                edit.CategoryId,
                edit.VenueId is { } venueId && venueId != Guid.Empty
                    ? venueId
                    : throw new InvalidOperationException($"Individual event {guideEvent.Id} edit requires a venue."),
                edit.Title, edit.Description, edit.StartAt, edit.DurationMinutes,
                edit.LocationNote, edit.Host, edit.IsRecurring, edit.RecurrenceDays);
        }
    }

    public async Task<BulkImportResult> BulkImportAsync(
        Guid campId, Guid submitterUserId, IReadOnlyList<BulkCsvRow> rows,
        LocalDate gateOpeningDate, int eventEndOffset, DateTimeZone timeZone,
        CancellationToken ct = default)
    {
        if (await GetOpenSubmissionSettingsAsync(ct) is not { } settings)
            return new BulkImportResult([], 0, 0, RefusalKey: "Events_SubmissionWindowClosed");
        return await new EventBulkImporter(repo, clock, localizer).ImportAsync(
            campId, submitterUserId, settings.EventSettingsId, rows, gateOpeningDate, eventEndOffset, timeZone, ct);
    }

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

    public async Task<ModerationOutcome> ApplyModerationAsync(
        Guid eventId, Guid actorUserId, EventModerationActionType actionType, string? reason,
        string? submitterEditUrl = null, CancellationToken ct = default)
    {
        if (actionType is EventModerationActionType.Rejected or EventModerationActionType.ResubmitRequested
            && string.IsNullOrWhiteSpace(reason))
            return ModerationOutcome.ReasonRequired;

        var guideEvent = await repo.GetEventForModerationAsync(eventId, ct)
            ?? throw new InvalidOperationException($"Event {eventId} not found.");

        if (guideEvent.Status is not EventStatus.Pending)
            return ModerationOutcome.NotPending;

        guideEvent.ApplyModerationAction(actionType, clock);

        var action = new EventModerationAction
        {
            Id = Guid.NewGuid(),
            GuideEventId = eventId,
            ActorUserId = actorUserId,
            Action = actionType,
            Reason = reason,
            CreatedAt = clock.GetCurrentInstant()
        };

        await repo.SaveEventAndModerationActionAsync(guideEvent, action, ct);

        if (submitterEditUrl is not null)
        {
            var lifecycleStatus = actionType switch
            {
                EventModerationActionType.Approved => (EventStatus?)EventStatus.Approved,
                EventModerationActionType.Rejected => EventStatus.Rejected,
                EventModerationActionType.ResubmitRequested => EventStatus.ResubmitRequested,
                _ => null
            };
            if (lifecycleStatus.HasValue)
                await SendLifecycleEmailAsync(guideEvent, lifecycleStatus.Value, reason, submitterEditUrl);
        }

        return ModerationOutcome.Applied;
    }

    public async Task<IReadOnlyList<EventInfo>> GetAllEventsForDashboardAsync(CancellationToken ct = default)
    {
        var guideEventId = await GetGuideEventIdAsync(ct);
        var events = await repo.GetAllEventsForDashboardAsync(ct);
        return events.Where(e => e.IsInEvent(guideEventId)).Select(EventInfo.FromEvent).ToList();
    }

    public async Task<byte[]> GetApprovedEventsForExportAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var settings = await repo.GetGuideSettingsAsync(ct);
        var events = (await repo.GetApprovedEventsAsync(null, null, null, null, [], ct))
            .Where(e => e.IsInEvent(settings?.EventSettingsId)).Select(EventInfo.FromEvent).ToList();
        var eventSettings = settings is null ? null : await settingsService.GetEventSettingsByIdAsync(settings.EventSettingsId, ct);
        var tz = GetTimeZone(eventSettings);
        var campsById = await LoadCampsByIdAsync(camps, eventSettings?.Id, ct);
        var submitters = await LoadSubmittersAsync(
            userService, events.Where(e => e.CampId == null).Select(e => e.SubmitterUserId).Distinct(), ct);

        var rows = new List<object?[]>();
        foreach (var e in events.OrderBy(e => e.StartAt))
        {
            var camp = e.CampId.HasValue ? campsById.GetValueOrDefault(e.CampId.Value) : null;
            var campName = ResolveCampName(camp) ?? "";
            var venueName = e.VenueName ?? "";
            var submitterName = e.CampId == null
                ? submitters.GetValueOrDefault(e.SubmitterUserId)?.BurnerName ?? ""
                : "";

            foreach (var occurrence in e.GetOccurrenceInstants(eventSettings?.GateOpeningDate, tz))
            {
                var local = ToLocalDateTime(occurrence, tz);
                rows.Add([
                    e.Id.ToString(),
                    e.Title,
                    e.Description,
                    e.CategoryName,
                    campName,
                    venueName,
                    submitterName,
                    e.LocationNote ?? "",
                    local.ToInvariantDate(),
                    local.ToInvariantTime(),
                    e.DurationMinutes,
                    e.IsRecurring ? "Yes" : "No",
                    e.PriorityRank?.ToString(CultureInfo.InvariantCulture) ?? "",
                    e.Status.ToString(),
                    ToLocalDateTime(e.SubmittedAt, tz).ToInvariantTimestamp(),
                    e.Host ?? "",
                ]);
            }
        }

        var bytes = HumansCsv.WriteBytes(csv =>
        {
            csv.WriteRow("Id", "Title", "Description", "Category", "CampName", "VenueName", "SubmitterName",
                "LocationNote", "Date", "StartTime", "DurationMinutes", "IsRecurring", "PriorityRank", "Status", "SubmittedAt", "Host");
            foreach (var row in rows)
            {
                csv.WriteRow(row);
            }
        });
        await audit.LogAsync(EventsAuditActions.EventGuideExported, "EventGuide", settings?.Id ?? Guid.Empty,
            $"Approved event guide CSV exported: {rows.Count} rows", actorUserId);
        return bytes;
    }

    private static EventCategoryView ToCategoryView(EventCategory c) => new(
        Id: c.Id,
        Name: c.Name,
        Slug: c.Slug,
        IsSensitive: c.IsSensitive,
        DisplayOrder: c.DisplayOrder,
        IsActive: c.IsActive);

    private static EventVenueView ToVenueView(EventVenue v) => new(
        Id: v.Id,
        Name: v.Name,
        Description: v.Description,
        LocationDescription: v.LocationDescription,
        DisplayOrder: v.DisplayOrder,
        IsActive: v.IsActive);

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
