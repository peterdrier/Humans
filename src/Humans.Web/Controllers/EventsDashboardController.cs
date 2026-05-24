using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Events;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Web.Filters;
using Humans.Web.Models.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using static Humans.Web.Helpers.EventsLookupHelpers;

using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleGroups.EventsAdminOrAdmin)]
[Route("Events/Dashboard")]
[ServiceFilter(typeof(EventsFeatureFilter))]
public class EventsDashboardController(IEventService guide, ICampServiceRead camps, IUserServiceRead userService)
    : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var guideSettings = await guide.GetGuideSettingsAsync();
        var eventSettings = guideSettings != null
            ? await guide.GetEventSettingsByIdAsync(guideSettings.EventSettingsId)
            : null;
        DateTimeZone? tz = eventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
            : null;

        var allEvents = await guide.GetAllEventsForDashboardAsync();

        var model = new EventsDashboardViewModel
        {
            TotalCount = allEvents.Count,
            PendingCount = allEvents.Count(e => e.Status == EventStatus.Pending),
            ApprovedCount = allEvents.Count(e => e.Status == EventStatus.Approved),
            RejectedCount = allEvents.Count(e => e.Status == EventStatus.Rejected),
            ResubmitRequestedCount = allEvents.Count(e => e.Status == EventStatus.ResubmitRequested),
            WithdrawnCount = allEvents.Count(e => e.Status == EventStatus.Withdrawn)
        };

        var approvedEvents = allEvents.Where(e => e.Status == EventStatus.Approved).ToList();
        var gateOpeningDate = eventSettings?.GateOpeningDate;
        var eventEndOffset = eventSettings?.EventEndOffset ?? 0;

        if (gateOpeningDate != null)
        {
            var dayCounts = new Dictionary<int, int>();
            for (var d = 0; d <= eventEndOffset; d++)
                dayCounts[d] = 0;

            foreach (var e in approvedEvents)
            {
                foreach (var occ in tz != null ? e.GetOccurrenceInstants(gateOpeningDate.Value, tz) : (IReadOnlyList<Instant>)[e.StartAt])
                {
                    var dayOffset = ComputeDayOffset(occ, gateOpeningDate.Value, tz);
                    if (dayCounts.ContainsKey(dayOffset))
                        dayCounts[dayOffset]++;
                }
            }

            model.CoverageByDay = dayCounts
                .OrderBy(kv => kv.Key)
                .Select(kv => new DayCoverageRow
                {
                    DayLabel = gateOpeningDate.Value.PlusDays(kv.Key).ToString("ddd d MMM", null),
                    ApprovedCount = kv.Value
                }).ToList();
        }

        var categories = await guide.GetActiveCategoriesAsync();
        model.CoverageByCategory = categories.Select(cat =>
        {
            var catEvents = allEvents.Where(e => e.CategoryId == cat.Id).ToList();
            return new CategoryCoverageRow
            {
                CategoryName = cat.Name,
                SubmittedCount = catEvents.Count,
                ApprovedCount = catEvents.Count(e => e.Status == EventStatus.Approved),
                PendingCount = catEvents.Count(e => e.Status == EventStatus.Pending),
                RejectedCount = catEvents.Count(e => e.Status == EventStatus.Rejected)
            };
        }).ToList();

        var campEvents = allEvents.Where(e => e.CampId.HasValue).ToList();
        var campsById = await LoadCampsByIdAsync(camps, gateOpeningDate?.Year);
        model.TopCamps = campEvents
            .GroupBy(e => e.CampId!.Value)
            .Select(g =>
            {
                var camp = campsById.GetValueOrDefault(g.Key);
                var seasonName = camp?.Active?.Name;
                return new CampSubmissionRow
                {
                    CampName = seasonName ?? camp?.Slug ?? "Unknown",
                    SubmittedCount = g.Count(),
                    ApprovedCount = g.Count(e => e.Status == EventStatus.Approved),
                    PendingCount = g.Count(e => e.Status == EventStatus.Pending)
                };
            })
            .OrderByDescending(c => c.SubmittedCount)
            .Take(20)
            .ToList();

        return View(model);
    }

    private static int ComputeDayOffset(Instant instant, LocalDate gateOpeningDate, DateTimeZone? tz)
    {
        LocalDate eventDate = tz != null
            ? instant.InZone(tz).Date
            : LocalDate.FromDateTime(instant.ToDateTimeUtc());
        return Period.Between(gateOpeningDate, eventDate, PeriodUnits.Days).Days;
    }
}
