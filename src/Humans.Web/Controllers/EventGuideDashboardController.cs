using Humans.Application.Interfaces.EventGuide;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Filters;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleGroups.GuideModeratorOrAdmin)]
[Route("Events/Dashboard")]
[ServiceFilter(typeof(EventGuideFeatureFilter))]
public class EventGuideDashboardController : HumansControllerBase
{
    private readonly IEventGuideService _guide;

    public EventGuideDashboardController(IEventGuideService guide, UserManager<User> userManager)
        : base(userManager)
    {
        _guide = guide;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var guideSettings = await _guide.GetGuideSettingsAsync();
        DateTimeZone? tz = guideSettings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId)
            : null;

        var allEvents = await _guide.GetAllEventsForDashboardAsync();

        var model = new GuideDashboardViewModel
        {
            TotalCount = allEvents.Count,
            PendingCount = allEvents.Count(e => e.Status == GuideEventStatus.Pending),
            ApprovedCount = allEvents.Count(e => e.Status == GuideEventStatus.Approved),
            RejectedCount = allEvents.Count(e => e.Status == GuideEventStatus.Rejected),
            ResubmitRequestedCount = allEvents.Count(e => e.Status == GuideEventStatus.ResubmitRequested),
            WithdrawnCount = allEvents.Count(e => e.Status == GuideEventStatus.Withdrawn)
        };

        var approvedEvents = allEvents.Where(e => e.Status == GuideEventStatus.Approved).ToList();
        var gateOpeningDate = guideSettings?.EventSettings?.GateOpeningDate;
        var eventEndOffset = guideSettings?.EventSettings?.EventEndOffset ?? 0;

        if (gateOpeningDate != null)
        {
            var dayCounts = new Dictionary<int, int>();
            for (var d = 0; d <= eventEndOffset; d++)
                dayCounts[d] = 0;

            foreach (var e in approvedEvents)
            {
                foreach (var occ in e.GetOccurrenceInstants())
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

        var categories = await _guide.GetActiveCategoriesAsync();
        model.CoverageByCategory = categories.Select(cat =>
        {
            var catEvents = allEvents.Where(e => e.CategoryId == cat.Id).ToList();
            return new CategoryCoverageRow
            {
                CategoryName = cat.Name,
                SubmittedCount = catEvents.Count,
                ApprovedCount = catEvents.Count(e => e.Status == GuideEventStatus.Approved),
                PendingCount = catEvents.Count(e => e.Status == GuideEventStatus.Pending),
                RejectedCount = catEvents.Count(e => e.Status == GuideEventStatus.Rejected)
            };
        }).ToList();

        var campEvents = allEvents.Where(e => e.CampId.HasValue).ToList();
        model.TopCamps = campEvents
            .GroupBy(e => e.CampId!.Value)
            .Select(g =>
            {
                var first = g.First();
                var season = first.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
                return new CampSubmissionRow
                {
                    CampName = season?.Name ?? first.Camp?.Slug ?? "Unknown",
                    SubmittedCount = g.Count(),
                    ApprovedCount = g.Count(e => e.Status == GuideEventStatus.Approved),
                    PendingCount = g.Count(e => e.Status == GuideEventStatus.Pending)
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
