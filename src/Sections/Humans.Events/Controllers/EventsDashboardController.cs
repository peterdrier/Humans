using Humans.Base.Extensions;
using Humans.Camps.Contracts;
using Humans.Events.Services;
using Humans.Base.Controllers;
using Humans.Events.Filters;
using Humans.Events.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static Humans.Events.Helpers.EventsLookupHelpers;
using static Humans.Events.Helpers.EventsTimeHelpers;

using Humans.Base.Authorization;
using Humans.Events.Contracts;
using Humans.Users.Contracts;

namespace Humans.Events.Controllers;

[Authorize(Policy = EventsPolicies.Read)]
[Route("Events/Dashboard")]
[ServiceFilter(typeof(EventsFeatureFilter))]
internal sealed class EventsDashboardController(IEventManagement guide, ICampService camps, IUserService userService)
    : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var guideSettings = await guide.GetGuideSettingsAsync(ct);
        var eventSettings = await LoadBurnSettingsAsync(guide, guideSettings, ct);
        var tz = GetTimeZone(eventSettings);

        var allEvents = await guide.GetAllEventsForDashboardAsync(ct);
        var statusCounts = await guide.GetEventStatusCountsAsync(ct);

        var model = new EventsDashboardViewModel
        {
            TotalCount = allEvents.Count,
            PendingCount = statusCounts.GetValueOrDefault(EventStatus.Pending),
            ApprovedCount = statusCounts.GetValueOrDefault(EventStatus.Approved),
            RejectedCount = statusCounts.GetValueOrDefault(EventStatus.Rejected),
            ResubmitRequestedCount = statusCounts.GetValueOrDefault(EventStatus.ResubmitRequested),
            WithdrawnCount = statusCounts.GetValueOrDefault(EventStatus.Withdrawn)
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
                foreach (var occ in e.GetOccurrenceInstants(gateOpeningDate, tz))
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
                    DayLabel = gateOpeningDate.Value.PlusDays(kv.Key).ToWeekdayDayMonth(),
                    ApprovedCount = kv.Value
                }).ToList();
        }

        var categories = await guide.GetActiveCategoriesAsync(ct);
        model.CoverageByCategory = categories.OrderBy(c => c.DisplayOrder).Select(cat =>
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
        var campsById = await LoadCampsByIdAsync(camps, gateOpeningDate?.Year, ct);
        model.TopCamps = campEvents
            .GroupBy(e => e.CampId!.Value)
            .Select(g =>
            {
                var camp = campsById.GetValueOrDefault(g.Key);
                return new CampSubmissionRow
                {
                    CampName = ResolveCampName(camp) ?? "Unknown",
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
}
