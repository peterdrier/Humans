using System.Globalization;
using System.Text.Json;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Calendar.Contracts;
using Humans.Events.Contracts;
using Humans.Events.Data;
using Humans.Events.Domain;
using Humans.Events.Services.Dtos;
using Humans.Settings.Contracts;
using Microsoft.Extensions.Localization;
using NodaTime;
using static Humans.Events.Helpers.EventsTimeHelpers;

namespace Humans.Events.Services;

internal sealed class EventPersonalSchedule(
    IEventRepository repo,
    ISettingsService settingsService,
    IClock clock,
    ILogger logger,
    IStringLocalizer<EventsResource> localizer) : IApplicationService
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

    public async Task<bool> AddFavouriteAsync(Guid userId, Guid eventId, int? dayOffset, CancellationToken ct = default)
    {
        var ev = await repo.GetApprovedEventByIdAsync(eventId, ct) is { } found
            && found.IsInEvent(await GetGuideEventIdAsync(ct))
            ? found
            : throw new KeyNotFoundException();
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
}
