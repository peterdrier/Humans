using System.Text.Json;
using Humans.Application.DTOs.Events;
using Humans.Application.Events;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.Events;

public sealed class EventService(IEventRepository repo, IBurnSettingsService burnSettings, IClock clock)
    : IEventService, IUserDataContributor
{
    // EventSettings is owned by Shifts; cross via IBurnSettingsService supplier API (§2c, #719).

    public async Task<EventGuideSettingsView?> GetGuideSettingsAsync(CancellationToken ct = default)
    {
        var settings = await repo.GetGuideSettingsAsync(ct);
        return settings is null ? null : await ToGuideSettingsViewAsync(settings, ct);
    }

    public async Task<bool> IsSubmissionOpenAsync(CancellationToken ct = default)
    {
        var settings = await repo.GetGuideSettingsAsync(ct);
        return settings?.IsSubmissionOpenAt(clock.GetCurrentInstant()) ?? false;
    }

    private async Task<EventGuideSettingsView> ToGuideSettingsViewAsync(EventGuideSettings settings, CancellationToken ct)
    {
        // TimeZoneId is stitched in from the Shifts-owned event_settings row via
        // IBurnSettingsService (cross-section supplier API, §2c / #719).
        var burn = await burnSettings.GetByIdAsync(settings.EventSettingsId, ct);
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

    public async Task<IReadOnlyList<BurnSettingsInfo>> GetEventSettingsOptionsAsync(CancellationToken ct = default)
    {
        // Invariant: at most one active burn; singleton list keeps admin picker forward-compatible.
        var active = await burnSettings.GetActiveAsync(ct);
        return active is null ? [] : [active];
    }

    public Task<BurnSettingsInfo?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default)
        => burnSettings.GetByIdAsync(id, ct);

    public async Task SaveGuideSettingsAsync(
        Guid? existingId, Guid eventSettingsId,
        LocalDateTime submissionOpenAt, LocalDateTime submissionCloseAt, LocalDateTime guidePublishAt,
        int maxPrintSlots, CancellationToken ct = default)
    {
        var burn = await burnSettings.GetByIdAsync(eventSettingsId, ct)
            ?? throw new InvalidOperationException($"EventSettings {eventSettingsId} not found.");

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

    public Task SubmitEventAsync(Event guideEvent, CancellationToken ct = default)
        => repo.AddEventAsync(guideEvent, ct);

    public Task UpdateAndResubmitAsync(Event guideEvent, CancellationToken ct = default)
    {
        if (guideEvent.Status is EventStatus.Pending)
        {
            guideEvent.LastUpdatedAt = clock.GetCurrentInstant();
        }
        else if (guideEvent.Status is EventStatus.Approved)
        {
            var now = clock.GetCurrentInstant();
            guideEvent.Status = EventStatus.Pending;
            guideEvent.SubmittedAt = now;
            guideEvent.LastUpdatedAt = now;
        }
        else
        {
            guideEvent.Submit(clock);
        }

        return repo.SaveEventAsync(guideEvent, ct);
    }

    public Task WithdrawEventAsync(Event guideEvent, CancellationToken ct = default)
    {
        guideEvent.Withdraw(clock);
        return repo.SaveEventAsync(guideEvent, ct);
    }

    public async Task<BulkImportResult> BulkImportAsync(
        Guid campId, Guid submitterUserId, IReadOnlyList<BulkCsvRow> rows,
        LocalDate gateOpeningDate, int eventEndOffset, DateTimeZone timeZone,
        CancellationToken ct = default)
    {
        var categories = await repo.GetActiveCategoriesAsync(ct);
        var existingEvents = await repo.GetCampSubmissionsAsync(campId, ct);

        var errors = ValidateBulkRows(rows, categories, existingEvents);
        if (errors.Count > 0)
            return new BulkImportResult(errors, 0, 0);

        var created = 0;
        var updated = 0;
        foreach (var row in rows)
        {
            var category = categories.First(c => string.Equals(c.Name, row.Category, StringComparison.OrdinalIgnoreCase));
            var date = NodaTime.Text.LocalDatePattern.Iso.Parse(row.Date).Value;
            var time = NodaTime.Text.LocalTimePattern.CreateWithInvariantCulture("HH:mm").Parse(row.StartTime).Value;
            var startAt = (date + time).InZoneLeniently(timeZone).ToInstant();
            var recurrenceOffsets = row.IsRecurring && !string.IsNullOrEmpty(row.RecurrenceDays)
                ? EventRecurrenceDays.DisplayDaysToOffsets(row.RecurrenceDays, gateOpeningDate, eventEndOffset)
                : null;

            if (row.Id.HasValue)
            {
                var existing = existingEvents.First(e => e.Id == row.Id.Value);

                // Compare recurrence by day-name set, not the raw offset string, so a
                // lossless round-trip ("0" ⇄ "Mon") isn't mistaken for an edit and the
                // event isn't needlessly re-queued for moderation.
                var existingDays = existing.IsRecurring && !string.IsNullOrEmpty(existing.RecurrenceDays)
                    ? EventRecurrenceDays.OffsetsToDisplayDays(existing.RecurrenceDays, gateOpeningDate)
                    : string.Empty;
                var rowDays = row.IsRecurring ? row.RecurrenceDays ?? string.Empty : string.Empty;

                var changed =
                    !string.Equals(existing.Title, row.Title, StringComparison.Ordinal) ||
                    !string.Equals(existing.Description, row.Description, StringComparison.Ordinal) ||
                    existing.CategoryId != category.Id ||
                    existing.StartAt != startAt ||
                    existing.DurationMinutes != row.DurationMinutes ||
                    !string.Equals(existing.LocationNote ?? string.Empty, row.LocationNote ?? string.Empty, StringComparison.Ordinal) ||
                    !string.Equals(existing.Host ?? string.Empty, row.Host ?? string.Empty, StringComparison.Ordinal) ||
                    existing.IsRecurring != row.IsRecurring ||
                    !EventRecurrenceDays.SameDays(existingDays, rowDays) ||
                    existing.PriorityRank != row.PriorityRank;

                if (!changed) continue;

                existing.Title = row.Title;
                existing.Description = row.Description;
                existing.CategoryId = category.Id;
                existing.StartAt = startAt;
                existing.DurationMinutes = row.DurationMinutes;
                existing.LocationNote = string.IsNullOrEmpty(row.LocationNote) ? null : row.LocationNote;
                existing.Host = string.IsNullOrEmpty(row.Host) ? null : row.Host;
                existing.IsRecurring = row.IsRecurring;
                existing.RecurrenceDays = row.IsRecurring ? recurrenceOffsets : null;
                existing.PriorityRank = row.PriorityRank;

                // One path for every existing status: UpdateAndResubmitAsync keeps a
                // Pending event Pending, re-queues an Approved one, and submits a
                // Draft/Rejected/ResubmitRequested one. (Withdrawn is rejected in
                // validation, so it never reaches here.)
                await UpdateAndResubmitAsync(existing, ct);
                updated++;
            }
            else
            {
                var newEvent = new Event
                {
                    Id = Guid.NewGuid(),
                    CampId = campId,
                    SubmitterUserId = submitterUserId,
                    CategoryId = category.Id,
                    Title = row.Title,
                    Description = row.Description,
                    LocationNote = string.IsNullOrEmpty(row.LocationNote) ? null : row.LocationNote,
                    Host = string.IsNullOrEmpty(row.Host) ? null : row.Host,
                    StartAt = startAt,
                    DurationMinutes = row.DurationMinutes,
                    IsRecurring = row.IsRecurring,
                    RecurrenceDays = recurrenceOffsets,
                    PriorityRank = row.PriorityRank
                };
                newEvent.Submit(clock);
                await SubmitEventAsync(newEvent, ct);
                created++;
            }
        }

        return new BulkImportResult([], created, updated);
    }

    private static List<BulkImportRowError> ValidateBulkRows(
        IReadOnlyList<BulkCsvRow> rows,
        IReadOnlyList<EventCategory> categories,
        IReadOnlyList<Event> existingEvents)
    {
        var errors = new List<BulkImportRowError>();
        foreach (var row in rows)
        {
            var rowErrors = new List<string>();

            if (string.IsNullOrWhiteSpace(row.Title)) rowErrors.Add("Title is required.");
            else if (row.Title.Length > 80) rowErrors.Add("Title must be 80 characters or fewer.");

            if (string.IsNullOrWhiteSpace(row.Description)) rowErrors.Add("Description is required.");
            else if (row.Description.Length > 450) rowErrors.Add("Description must be 450 characters or fewer.");

            if (row.LocationNote?.Length > 120) rowErrors.Add("LocationNote must be 120 characters or fewer.");
            if (row.Host?.Length > 40) rowErrors.Add("Host must be 40 characters or fewer.");

            if (string.IsNullOrWhiteSpace(row.Category)) rowErrors.Add("Category is required.");
            else if (!categories.Any(c => string.Equals(c.Name, row.Category, StringComparison.OrdinalIgnoreCase)))
                rowErrors.Add($"Category '{row.Category}' is not a valid active category.");

            if (string.IsNullOrWhiteSpace(row.Date)) rowErrors.Add("Date is required.");
            else if (!NodaTime.Text.LocalDatePattern.Iso.Parse(row.Date).Success)
                rowErrors.Add("Date must be in yyyy-MM-dd format.");

            if (string.IsNullOrWhiteSpace(row.StartTime)) rowErrors.Add("StartTime is required.");
            else if (!NodaTime.Text.LocalTimePattern.CreateWithInvariantCulture("HH:mm").Parse(row.StartTime).Success)
                rowErrors.Add("StartTime must be in HH:mm format.");

            if (row.DurationMinutes < 15 || row.DurationMinutes > 480)
                rowErrors.Add("DurationMinutes must be between 15 and 480.");
            else if (row.DurationMinutes % 15 != 0)
                rowErrors.Add("DurationMinutes must be a multiple of 15.");

            if (row.PriorityRank < 1 || row.PriorityRank > 100)
                rowErrors.Add("PriorityRank must be between 1 and 100.");

            if (row.Id.HasValue)
            {
                var existing = existingEvents.FirstOrDefault(e => e.Id == row.Id.Value);
                if (existing == null)
                    rowErrors.Add($"Event {row.Id.Value} not found for this barrio.");
                else if (existing.Status == EventStatus.Withdrawn)
                    rowErrors.Add("Withdrawn events cannot be updated via bulk upload.");
            }

            if (rowErrors.Count > 0)
                errors.Add(new BulkImportRowError(row.RowNumber, row.Title, rowErrors));
        }
        return errors;
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

    public Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default)
        => repo.GetFavouriteEventIdsAsync(userId, ct);

    public async Task<IReadOnlyList<EventFavouriteInfo>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default)
    {
        var favourites = await repo.GetFavouritesWithEventsAsync(userId, ct);
        return favourites.Select(f => new EventFavouriteInfo(
            f.Id, f.UserId, f.GuideEventId, f.CreatedAt, ToEventInfo(f.Event))).ToList();
    }

    public Task ToggleFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default)
        => repo.ToggleFavouriteAsync(userId, eventId, BuildFavourite(userId, eventId), ct);

    public Task<bool> AddFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default)
        => repo.AddFavouriteIfAbsentAsync(BuildFavourite(userId, eventId), ct);

    public Task<bool> RemoveFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default)
        => repo.RemoveFavouriteAsync(userId, eventId, ct);

    public async Task<List<string>> GetExcludedCategorySlugsAsync(Guid userId, CancellationToken ct = default)
    {
        var pref = await repo.GetPreferenceAsync(userId, ct);
        if (pref == null) return [];
        return JsonSerializer.Deserialize<List<string>>(pref.ExcludedCategorySlugs) ?? [];
    }

    public async Task<EventPreferenceInfo?> GetPreferenceAsync(Guid userId, CancellationToken ct = default)
    {
        var pref = await repo.GetPreferenceAsync(userId, ct);
        return pref is null ? null : new EventPreferenceInfo(pref.UserId, pref.ExcludedCategorySlugs, pref.UpdatedAt);
    }

    public Task SavePreferenceAsync(Guid userId, List<string> slugs, CancellationToken ct = default)
        => repo.UpsertPreferenceAsync(userId, JsonSerializer.Serialize(slugs), clock.GetCurrentInstant(), ct);

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
        Guid eventId, Guid actorUserId, EventModerationActionType actionType, string? reason, CancellationToken ct = default)
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

    private EventFavourite BuildFavourite(Guid userId, Guid eventId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        GuideEventId = eventId,
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

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var favourites = await repo.GetFavouritesForContributorAsync(userId, ct);
        var preference = await repo.GetPreferenceAsync(userId, ct);

        var shaped = new
        {
            Favourites = favourites
                .OrderBy(f => f.CreatedAt)
                .Select(f => new
                {
                    f.GuideEventId,
                    CreatedAt = f.CreatedAt.ToInvariantInstantString()
                }).ToList(),
            Preference = preference == null ? null : new
            {
                preference.ExcludedCategorySlugs,
                UpdatedAt = preference.UpdatedAt.ToInvariantInstantString()
            }
        };

        return [new UserDataSlice(GdprExportSections.Events, shaped)];
    }
}
