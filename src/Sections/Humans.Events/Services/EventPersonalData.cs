using System.Globalization;
using System.Text.Json;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Calendar.Contracts;
using Humans.Events.Contracts;
using Humans.Events.Data;
using Humans.Events.Domain;
using Humans.Events.Services.Dtos;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Humans.Settings.Contracts;
using Microsoft.Extensions.Localization;
using NodaTime;
using static Humans.Events.Helpers.EventsTimeHelpers;

namespace Humans.Events.Services;

internal sealed class EventPersonalData(
    IEventRepository repo,
    ISettingsService settingsService,
    IClock clock,
    ILogger logger,
    IStringLocalizer<EventsResource> localizer,
    IUserService userService) : IApplicationService
{
    public Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default)
        => repo.GetFavouriteEventIdsAsync(userId, ct);

    public async Task<IReadOnlyList<EventFavouriteInfo>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default) =>
        (await LoadFavouritesWithCalendarAsync(userId, ct)).Favourites;

    private async Task<(IReadOnlyList<EventFavouriteInfo> Favourites, EventSettingsInfo? Calendar)> LoadFavouritesWithCalendarAsync(
        Guid userId, CancellationToken ct)
    {
        var guideSettings = await repo.GetGuideSettingsAsync(ct);
        var guideEventId = guideSettings?.EventSettingsId;
        var favourites = await repo.GetFavouritesWithEventsAsync(userId, ct);
        if (favourites.Count == 0) return ([], null);

        var eventSettings = guideSettings is null
            ? null
            : await settingsService.GetEventSettingsByIdAsync(guideSettings.EventSettingsId, ct);
        var canExpand = GetTimeZone(eventSettings) is not null;
        var projected = favourites.Where(f => f.Event.IsInEvent(guideEventId)).Select(f => new EventFavouriteInfo(
                f.Id, f.UserId, f.GuideEventId, f.DayOffset, f.CreatedAt, EventInfo.FromEvent(f.Event)))
            .GroupBy(f => f.GuideEventId)
            .SelectMany(group =>
            {
                // A recurrence edit can leave several day bookmarks for one single event.
                // Project a whole-event target so the existing remove path clears them all.
                var wholeEvent = group.FirstOrDefault(f => !canExpand || !f.Event.IsRecurring
                    || string.IsNullOrWhiteSpace(f.Event.RecurrenceDays) || f.DayOffset is null);
                return wholeEvent is null ? group.ToArray() : [wholeEvent with { DayOffset = null }];
            }).ToList();
        return (projected, eventSettings);
    }

    public async Task<AddFavouriteOutcome> AddFavouriteAsync(Guid userId, Guid eventId, int? dayOffset, CancellationToken ct = default)
    {
        var ev = await repo.GetApprovedEventByIdAsync(eventId, ct);
        if (ev is null || !ev.IsInEvent(await GetGuideEventIdAsync(ct)))
            return AddFavouriteOutcome.EventUnavailable;
        if (dayOffset is { } day && ev.IsRecurring && !string.IsNullOrWhiteSpace(ev.RecurrenceDays)
            && !ev.RecurrenceDays
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset)
                    && offset == day))
        {
            return AddFavouriteOutcome.InvalidDay;
        }

        return await repo.AddFavouriteIfAbsentAsync(BuildFavourite(userId, eventId, dayOffset), ct)
            ? AddFavouriteOutcome.Added
            : AddFavouriteOutcome.AlreadyExists;
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

    private EventFavourite BuildFavourite(Guid userId, Guid eventId, int? dayOffset) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        GuideEventId = eventId,
        DayOffset = dayOffset,
        CreatedAt = clock.GetCurrentInstant()
    };

    public async Task<IReadOnlyList<CalendarFeedItem>> GetCalendarItemsForUserAsync(Guid userId, CancellationToken ct)
    {
        // Moderation can drop an event out of the feed without touching the favourite row.
        var (favourites, burn) = await LoadFavouritesWithCalendarAsync(userId, ct);
        var approved = favourites.Where(f => f.Event.Status == EventStatus.Approved).ToList();
        if (approved.Count == 0) return [];

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

            var location = string.Join(" — ", new[] { e.VenueName, e.LocationNote }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            var description = string.Join("\n\n", new[]
            {
                e.Description,
                string.IsNullOrWhiteSpace(e.Host) ? null : $"{localizer["Events_Submission_FieldHost"]}: {e.Host}",
                string.IsNullOrWhiteSpace(e.CategoryName) ? null : $"{localizer["Events_Submission_FieldCategory"]}: {e.CategoryName}",
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

    /// <summary>The event the guide is configured for; null when the guide has no settings yet.</summary>
    internal async Task<Guid?> GetGuideEventIdAsync(CancellationToken ct) =>
        (await repo.GetGuideSettingsAsync(ct))?.EventSettingsId;

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var info = await userService.GetUserInfoAsync(userId, ct);
        var userIds = info?.AllUserIds ?? [userId];
        var favourites = new List<EventFavourite>();
        var preferences = new List<EventPreference>();
        foreach (var id in userIds)
        {
            favourites.AddRange(await repo.GetFavouritesForContributorAsync(id, ct));
            if (await repo.GetPreferenceAsync(id, ct) is { } preference)
                preferences.Add(preference);
        }
        var submissions = (await repo.GetAllEventsForDashboardAsync(ct))
            .Where(e => userIds.Contains(e.SubmitterUserId));
        var requestedPreference = preferences.FirstOrDefault(p => p.UserId == userId);

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
            Preference = requestedPreference is null ? null : new
            {
                requestedPreference.ExcludedCategorySlugs,
                UpdatedAt = requestedPreference.UpdatedAt.ToIso8601()
            },
            AdditionalPreferences = preferences.Where(p => p.UserId != userId).OrderBy(p => p.UpdatedAt).Select(p => new
            {
                p.UserId,
                p.ExcludedCategorySlugs,
                UpdatedAt = p.UpdatedAt.ToIso8601()
            }).ToList()
        };

        return [new UserDataSlice(Events, shaped)];
    }

    internal const string Events = "Events";
}
