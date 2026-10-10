using System.Text.Json;
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
    IUserServiceRead userService,
    IEmailService emailService,
    EventsEmails emailMessages,
    IClock clock,
    ILogger<EventService> logger,
    IStringLocalizer<EventsResource> localizer)
    // IUserDataContributor is implemented by CachingEventService, which delegates here —
    // erasure edits cached rows, so the fan-out has to run through the decorator.
    : IEventService, ICalendarFeedContributor
{
    public async Task<EventGuideSettingsView?> GetGuideSettingsAsync(CancellationToken ct = default)
    {
        var settings = await repo.GetGuideSettingsAsync(ct);
        return settings is null ? null : await ToGuideSettingsViewAsync(settings, ct);
    }

    private async Task<EventGuideSettingsView> ToGuideSettingsViewAsync(EventGuideSettings settings, CancellationToken ct)
    {
        // TimeZoneId is stitched in from the Settings-owned settings_event row via
        // ISettingsService (cross-section supplier API, nobodies-collective/Humans#719).
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
        // Invariant: at most one active burn; singleton list keeps admin picker forward-compatible.
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
        var events = await repo.GetUserSubmissionsAsync(userId, ct);
        return events.Select(ToEventInfo).ToList();
    }

    public Task<Event?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default)
        => repo.GetUserEventAsync(eventId, userId, ct);

    public async Task<IReadOnlyList<EventInfo>> GetCampSubmissionsAsync(Guid campId, CancellationToken ct = default)
    {
        var events = await repo.GetCampSubmissionsAsync(campId, ct);
        return events.Select(ToEventInfo).ToList();
    }

    public Task<Event?> GetCampEventAsync(Guid eventId, Guid campId, CancellationToken ct = default)
        => repo.GetCampEventAsync(eventId, campId, ct);

    public async Task<CampSubmissionsSummary> GetCampSubmissionsSummaryAsync(Guid campId, CancellationToken ct = default)
    {
        var events = await GetCampSubmissionsAsync(campId, ct);
        return new CampSubmissionsSummary(
            SubmittedCount: events.Count,
            ApprovedCount: events.Count(e => e.Status == EventStatus.Approved),
            PendingCount: events.Count(e => e.Status == EventStatus.Pending),
            Events: events);
    }

    public async Task SubmitEventAsync(Event guideEvent, string? lifecycleActionUrl = null, CancellationToken ct = default)
    {
        await repo.AddEventAsync(guideEvent, ct);

        // Submission-confirmation email is part of the submit workflow. A null
        // actionUrl opts out (bulk import: one email per CSV row would spam the
        // uploading event manager).
        if (lifecycleActionUrl is not null)
            await SendLifecycleEmailAsync(guideEvent, EventStatus.Pending, reason: null, lifecycleActionUrl, ct);
    }

    private async Task SendLifecycleEmailAsync(
        Event guideEvent, EventStatus newStatus, string? reason, string actionUrl, CancellationToken ct)
    {
        // The mutation is already persisted by the caller — failures preparing or
        // sending email must not report a failed submit/moderation operation.
        try
        {
            var submitter = await userService.GetUserInfoAsync(guideEvent.SubmitterUserId, ct);
            if (submitter?.Email is null)
            {
                logger.LogWarning(
                    "Skipping lifecycle email for event {EventId}: submitter {SubmitterId} has no notification email",
                    guideEvent.Id, guideEvent.SubmitterUserId);
                return;
            }

            var submitterEmail = submitter.Email;
            var language = submitter.PreferredLanguage;

            await emailService.SendAsync(emailMessages.EventLifecycle(
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

    public Task UpdateAndResubmitAsync(Event guideEvent, CancellationToken ct = default)
    {
        guideEvent.Resubmit(clock);
        return repo.SaveEventAsync(guideEvent, ct);
    }

    public Task WithdrawEventAsync(Event guideEvent, CancellationToken ct = default)
    {
        guideEvent.Withdraw(clock);
        return repo.SaveEventAsync(guideEvent, ct);
    }

    public Task AdminUpdateAsync(Event guideEvent, Guid actorUserId, string? note, CancellationToken ct = default)
    {
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

        return repo.SaveEventAndModerationActionAsync(guideEvent, action, ct);
    }

    public Task<BulkImportResult> BulkImportAsync(
        Guid campId, Guid submitterUserId, IReadOnlyList<BulkCsvRow> rows,
        LocalDate gateOpeningDate, int eventEndOffset, DateTimeZone timeZone,
        CancellationToken ct = default) =>
        new EventBulkImporter(repo, clock, localizer).ImportAsync(
            campId, submitterUserId, rows, gateOpeningDate, eventEndOffset, timeZone, ct);

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
        var events = await repo.GetApprovedEventsAsync(campId, venueId, categoryId, q, excludedSlugs, ct);
        return events.Select(ToApprovedEventView).ToList();
    }

    public async Task<ApprovedEventView?> GetApprovedEventByIdAsync(Guid id, CancellationToken ct = default)
    {
        var ev = await repo.GetApprovedEventByIdAsync(id, ct);
        return ev is null ? null : ToApprovedEventView(ev);
    }

    // Event search is served from the cached approved-event snapshot in
    // CachingEventService — it must never hit the DB. Reaching the inner service
    // means a DI registration mistake. Mirrors CachingTeamService.SearchAsync /
    // CachingCampService.SearchAsync (search is cache-only; there is no repository search).
    public Task<IReadOnlyList<EventSearchHit>> SearchAsync(
        string query, int max, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Event search runs against the cached approved-event snapshot in " +
            "CachingEventService. If this is being called on the inner Service it " +
            "indicates a DI registration mistake — IEventServiceRead must resolve to " +
            "the caching decorator.");

    public Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default)
        => repo.GetFavouriteEventIdsAsync(userId, ct);

    public async Task<IReadOnlyList<EventFavouriteInfo>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default)
    {
        var favourites = await repo.GetFavouritesWithEventsAsync(userId, ct);
        return favourites.Select(f => new EventFavouriteInfo(
            f.Id, f.UserId, f.GuideEventId, f.DayOffset, f.CreatedAt, ToEventInfo(f.Event))).ToList();
    }

    public async Task<bool> AddFavouriteAsync(Guid userId, Guid eventId, int? dayOffset, CancellationToken ct = default)
    {
        var ev = await repo.GetApprovedEventByIdAsync(eventId, ct)
            ?? throw new KeyNotFoundException();
        if (dayOffset is { } day && ev.IsRecurring && !string.IsNullOrWhiteSpace(ev.RecurrenceDays)
            && !ev.RecurrenceDays
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset)
                    && offset == day))
        {
            throw new ArgumentOutOfRangeException(nameof(dayOffset));
        }

        return await repo.AddFavouriteIfAbsentAsync(BuildFavourite(userId, eventId, dayOffset), ct);
    }

    public Task<bool> RemoveFavouriteAsync(Guid userId, Guid eventId, int? dayOffset, CancellationToken ct = default)
        => repo.RemoveFavouriteAsync(userId, eventId, dayOffset, ct);

    public async Task<List<string>> GetExcludedCategorySlugsAsync(Guid userId, CancellationToken ct = default)
    {
        var pref = await repo.GetPreferenceAsync(userId, ct);
        if (pref == null) return [];
        return (JsonSerializer.Deserialize<List<string>>(pref.ExcludedCategorySlugs) ?? [])
            .Select(slug => slug.ToLowerInvariant()).ToList();
    }

    public Task SavePreferenceAsync(Guid userId, List<string> slugs, CancellationToken ct = default)
        => repo.UpsertPreferenceAsync(userId,
            JsonSerializer.Serialize(slugs.Select(slug => slug.ToLowerInvariant())), clock.GetCurrentInstant(), ct);

    public Task<Dictionary<EventStatus, int>> GetEventStatusCountsAsync(CancellationToken ct = default)
        => repo.GetModerationStatusCountsAsync(ct);

    public async Task<IReadOnlyList<EventInfo>> GetEventsByStatusAsync(EventStatus status, CancellationToken ct = default)
    {
        var events = await repo.GetEventsByStatusAsync(status, ct);
        return events.Select(ToEventInfo).ToList();
    }

    public Task<Event?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default)
        => repo.GetEventForModerationAsync(eventId, ct);

    public Task<IReadOnlyList<CampEventOverlap>> GetCampEventsForOverlapAsync(CancellationToken ct = default)
        => repo.GetActiveCampEventsAsync(ct);

    public async Task ApplyModerationAsync(
        Guid eventId, Guid actorUserId, EventModerationActionType actionType, string? reason,
        string? submitterEditUrl = null, CancellationToken ct = default)
    {
        var guideEvent = await repo.GetEventForModerationAsync(eventId, ct)
            ?? throw new InvalidOperationException($"Event {eventId} not found.");

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

        // Notifying the submitter of the decision is part of the moderation
        // workflow; the URL is the caller's routing concern (null opts out).
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
                await SendLifecycleEmailAsync(guideEvent, lifecycleStatus.Value, reason, submitterEditUrl, ct);
        }
    }

    public async Task<IReadOnlyList<EventInfo>> GetAllEventsForDashboardAsync(CancellationToken ct = default)
    {
        var events = await repo.GetAllEventsForDashboardAsync(ct);
        return events.Select(ToEventInfo).ToList();
    }

    public async Task<ApprovedEventsExportInfo> GetApprovedEventsForExportAsync(CancellationToken ct = default)
    {
        var settings = await repo.GetGuideSettingsAsync(ct);
        var events = await repo.GetApprovedEventsAsync(null, null, null, null, [], ct);
        var settingsView = settings is null ? null : await ToGuideSettingsViewAsync(settings, ct);
        return new ApprovedEventsExportInfo(events.Select(ToEventInfo).ToList(), settingsView);
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

    // Tolerates a null Category nav (the dashboard query includes Category, but
    // project defensively) and an unloaded EventVenue / moderation-history nav.
    private static EventInfo ToEventInfo(Event e) => new(
        Id: e.Id,
        CampId: e.CampId,
        GuideSharedVenueId: e.GuideSharedVenueId,
        SubmitterUserId: e.SubmitterUserId,
        CategoryId: e.CategoryId,
        CategoryName: e.Category?.Name ?? string.Empty,
        CategorySlug: e.Category?.Slug ?? string.Empty,
        CategoryIsSensitive: e.Category?.IsSensitive ?? false,
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
        Status: e.Status,
        SubmittedAt: e.SubmittedAt,
        LastUpdatedAt: e.LastUpdatedAt,
        ModerationHistory: e.EventModerationActions
            .Select(a => new EventModerationHistoryInfo(a.ActorUserId, a.Action, a.Reason, a.CreatedAt))
            .ToList());

    private EventFavourite BuildFavourite(Guid userId, Guid eventId, int? dayOffset) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        GuideEventId = eventId,
        DayOffset = dayOffset,
        CreatedAt = clock.GetCurrentInstant()
    };

    private static Instant ToInstant(LocalDateTime localDateTime, DateTimeZone? tz)
    {
        if (tz == null)
        {
            var utc = DateTime.SpecifyKind(localDateTime.ToDateTimeUnspecified(), DateTimeKind.Utc);
            return Instant.FromDateTimeUtc(utc);
        }
        return localDateTime.InZoneLeniently(tz).ToInstant();
    }

    // Nothing public to contribute to the community calendar yet.
    public Task<IReadOnlyList<CalendarFeedItem>> GetPublicItemsForWindowAsync(Instant from, Instant to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CalendarFeedItem>>([]);

    public async Task<IReadOnlyList<CalendarFeedItem>> GetCalendarItemsForUserAsync(Guid userId, CancellationToken ct)
    {
        // Favourited events that are still Approved — moderation changes drop
        // an event out of the feed without touching the favourite row.
        var favourites = await repo.GetFavouritesWithEventsAsync(userId, ct);
        var approved = favourites.Where(f => f.Event.Status == EventStatus.Approved).ToList();
        if (approved.Count == 0) return [];

        // Recurrence expansion needs the burn's gate date + timezone, stitched
        // cross-section via ISettingsService (§2c) like the guide settings view.
        var guideSettings = await repo.GetGuideSettingsAsync(ct);
        var burn = guideSettings is null ? null : await settingsService.GetEventSettingsByIdAsync(guideSettings.EventSettingsId, ct);
        var tz = burn is null ? null : DateTimeZoneProviders.Tzdb.GetZoneOrNull(burn.TimeZoneId);
        if (burn is not null && tz is null)
        {
            logger.LogWarning(
                "Burn settings {EventSettingsId} have unknown timezone {TimeZoneId}; iCal feed falls back to single occurrences keyed by UTC date",
                burn.Id,
                burn.TimeZoneId);
        }

        var items = new List<CalendarFeedItem>();
        foreach (var favourite in approved)
        {
            var e = favourite.Event;
            var occurrences = e.GetOccurrenceInstants(burn?.GateOpeningDate, tz, favourite.DayOffset);

            var location = string.Join(" — ", new[] { e.EventVenue?.Name, e.LocationNote }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            var description = string.Join("\n\n", new[]
            {
                e.Description,
                string.IsNullOrWhiteSpace(e.Host) ? null : $"{localizer["Events_Submission_FieldHost"]}: {e.Host}",
                string.IsNullOrWhiteSpace(e.Category?.Name) ? null : $"{localizer["Events_Submission_FieldCategory"]}: {e.Category.Name}",
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

            foreach (var start in occurrences)
            {
                var dateKey = DateFormattingExtensions.IcalBasicDatePattern.Format(start.InZone(tz ?? DateTimeZone.Utc).Date);
                items.Add(new CalendarFeedItem(
                    Uid: $"event-{e.Id}-{dateKey}@humans.nobodies.team",
                    Source: "Events",
                    Summary: e.Title,
                    Description: description.Length == 0 ? null : description,
                    Start: start,
                    End: start.Plus(Duration.FromMinutes(e.DurationMinutes)),
                    Location: location.Length == 0 ? null : location,
                    // No per-event public page; the schedule is where favourites live.
                    Url: "/Events/Schedule"));
            }
        }

        return items;
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var favourites = await repo.GetFavouritesForContributorAsync(userId, ct);
        var preference = await repo.GetPreferenceAsync(userId, ct);
        var submissions = (await repo.GetAllEventsForDashboardAsync(ct))
            .Where(e => e.SubmitterUserId == userId);

        var shaped = new
        {
            SubmittedEvents = submissions
                .OrderBy(e => e.SubmittedAt)
                .ThenBy(e => e.Id)
                .Select(e => new
                {
                    e.Id,
                    e.CampId,
                    e.GuideSharedVenueId,
                    e.CategoryId,
                    e.Title,
                    e.Description,
                    e.LocationNote,
                    e.Host,
                    StartAt = e.StartAt.ToIso8601(),
                    e.DurationMinutes,
                    e.IsRecurring,
                    e.RecurrenceDays,
                    e.PriorityRank,
                    Status = e.Status.ToString(),
                    SubmittedAt = e.SubmittedAt.ToIso8601(),
                    LastUpdatedAt = e.LastUpdatedAt.ToIso8601()
                }).ToList(),
            Favourites = favourites
                .OrderBy(f => f.CreatedAt)
                .Select(f => new
                {
                    f.GuideEventId,
                    f.DayOffset,
                    CreatedAt = f.CreatedAt.ToIso8601()
                }).ToList(),
            Preference = preference == null ? null : new
            {
                preference.ExcludedCategorySlugs,
                UpdatedAt = preference.UpdatedAt.ToIso8601()
            }
        };

        return [new UserDataSlice(Events, shaped)];
    }

    internal const string Events = "Events";

    // internal: CachingEventService carries IUserDataContributor and returns this table
    // from an uninitialized instance, so it has to be static and reachable from there.
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

    /// <summary>
    /// Favourites and the category-exclusion preference go; submitted events stay, with the
    /// person's <c>Host</c> display name cleared. See <see cref="Erasure"/> for why.
    /// </summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        await repo.DeleteFavouritesAndPreferenceForUserAsync(userId, ct);
        await repo.ClearSubmitterHostForUserAsync(userId, ct);
    }
}
