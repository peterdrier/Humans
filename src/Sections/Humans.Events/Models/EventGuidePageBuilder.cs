using Humans.Base.Extensions;
using Humans.Camps.Contracts;
using Humans.Events.Services;
using NodaTime;
using static Humans.Events.Helpers.EventsLookupHelpers;
using static Humans.Events.Helpers.EventsTimeHelpers;

namespace Humans.Events.Models;

internal sealed class EventGuidePageBuilder(IEventManagement guide, ICampService camps)
{
    public async Task<ScheduleViewModel> BuildScheduleAsync(Guid userId, CancellationToken ct = default)
    {
        var guideSettings = await guide.GetGuideSettingsAsync(ct);
        var eventSettings = await LoadBurnSettingsAsync(guide, guideSettings, ct);
        var tz = GetTimeZone(eventSettings);

        var gateOpeningDate = eventSettings?.GateOpeningDate;
        var favourites = await guide.GetFavouritesWithEventsAsync(userId, ct);
        var campsById = await LoadCampsByIdAsync(camps, eventSettings?.Id, ct);

        var scheduleItems = favourites.SelectMany(f =>
        {
            var e = f.Event;
            var camp = e.CampId.HasValue ? campsById.GetValueOrDefault(e.CampId.Value) : null;
            var campName = ResolveCampName(camp);

            IReadOnlyList<Instant> occurrences = e.GetOccurrenceInstants(gateOpeningDate, tz, f.DayOffset);

            return occurrences.Select(startInstant =>
            {
                var localStart = ToLocalDateTime(startInstant, tz);

                var dayOffset = ComputeDayOffset(startInstant, gateOpeningDate, tz);

                return new ScheduleItemViewModel
                {
                    EventId = e.Id,
                    Title = e.Title,
                    CategoryName = e.CategoryName,
                    CampName = campName,
                    VenueName = e.VenueName,
                    LocationNote = e.LocationNote,
                    StartAt = localStart,
                    DurationMinutes = e.DurationMinutes,
                    DayOffset = dayOffset,
                    DayLabel = gateOpeningDate != null
                        ? gateOpeningDate.Value.PlusDays(dayOffset).ToWeekdayDayMonth()
                        : localStart.ToWeekdayDayMonth(),
                    StartInstant = startInstant,
                    FavouriteDayOffset = f.DayOffset,
                    HasConflict = false
                };
            });
        }).OrderBy(i => i.StartInstant).ToList();

        foreach (var index in EventConflictDetector.FindConflictingIndexes(
                     scheduleItems, i => i.StartInstant, i => i.DurationMinutes))
        {
            scheduleItems[index].HasConflict = true;
        }

        var model = new ScheduleViewModel
        {
            TimeZoneId = eventSettings?.TimeZoneId,
            DayGroups = scheduleItems
                .GroupBy(i => i.DayOffset)
                .OrderBy(g => g.Key)
                .Select(g => new ScheduleDayGroup
                {
                    DayLabel = g.First().DayLabel,
                    Items = g.OrderBy(i => i.StartAt).ToList()
                }).ToList()
        };

        return model;
    }

    public async Task<BrowseViewModel> BuildBrowseAsync(
        Guid userId, int[]? days, Guid? categoryId, Guid? venueId, string? q,
        bool favouritesOnly, CancellationToken ct = default)
    {
        var guideSettings = await guide.GetGuideSettingsAsync(ct);
        var eventSettings = await LoadBurnSettingsAsync(guide, guideSettings, ct);
        var tz = GetTimeZone(eventSettings);

        var gateOpeningDate = eventSettings?.GateOpeningDate;
        var filterDays = days != null && days.Length > 0 ? days.ToHashSet() : null;

        var excludedSlugs = await guide.GetExcludedCategorySlugsAsync(userId, ct);
        var favouriteDaysByEventId = (await guide.GetFavouritesWithEventsAsync(userId, ct))
            .ToLookup(f => f.GuideEventId, f => f.DayOffset);
        var events = await guide.GetApprovedEventsAsync(null, venueId, categoryId, q, excludedSlugs, ct);

        var campsById = await LoadCampsByIdAsync(camps, eventSettings?.Id, ct);

        var occurrences = EventOccurrenceExpander.Expand(
            events, favouriteDaysByEventId, gateOpeningDate, tz, filterDays);

        var items = occurrences.Select(o =>
        {
            var e = o.Event;
            var camp = e.CampId.HasValue ? campsById.GetValueOrDefault(e.CampId.Value) : null;
            var campName = ResolveCampName(camp);

            return new BrowseEventItem
            {
                EventId = e.Id,
                Title = e.Title,
                Description = e.Description,
                CategoryName = e.CategoryName,
                CampName = campName,
                VenueName = e.VenueName,
                LocationNote = e.LocationNote,
                StartAt = ToLocalDateTime(o.StartAt, tz),
                DurationMinutes = e.DurationMinutes,
                DayOffset = o.DayOffset,
                FavouriteDayOffset = o.FavouriteDayOffset,
                IsFavourited = o.IsFavourited,
                SubmitterUserId = e.CampId == null ? e.SubmitterUserId : null,
                Host = e.Host
            };
        }).ToList();

        if (favouritesOnly)
            items = items.Where(i => i.IsFavourited).ToList();

        var categories = await guide.GetActiveCategoriesAsync(ct);
        var venues = await guide.GetActiveVenuesAsync(ct);

        var eventDays = eventSettings != null ? BuildEventDayOptions(eventSettings) : [];

        var model = new BrowseViewModel
        {
            TimeZoneId = eventSettings?.TimeZoneId,
            Categories = categories.OrderBy(c => c.DisplayOrder).Select(c => new CategoryOptionViewModel { Id = c.Id, Name = c.Name }).ToList(),
            Venues = venues.OrderBy(v => v.DisplayOrder).Select(v => new VenueOptionViewModel { Id = v.Id, Name = v.Name }).ToList(),
            Days = eventDays,
            FilterDays = filterDays ?? [],
            FilterCategoryId = categoryId,
            FilterVenueId = venueId,
            SearchQuery = q,
            FavouritesOnly = favouritesOnly,
            DayGroups = items
                .GroupBy(i => i.DayOffset)
                .OrderBy(g => g.Key)
                .Select(g => new BrowseDayGroup
                {
                    DayOffset = g.Key,
                    DayLabel = gateOpeningDate != null
                        ? gateOpeningDate.Value.PlusDays(g.Key).ToWeekdayDayMonth()
                        : g.First().StartAt.ToWeekdayDayMonth(),
                    Items = g.OrderBy(i => i.StartAt).ToList()
                }).ToList()
        };

        return model;
    }
}
