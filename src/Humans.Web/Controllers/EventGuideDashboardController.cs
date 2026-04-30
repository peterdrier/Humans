using System.Globalization;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Web.Filters;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleGroups.GuideModeratorOrAdmin)]
[Route("EventGuide/Dashboard")]
[ServiceFilter(typeof(EventGuideFeatureFilter))]
public class EventGuideDashboardController : HumansControllerBase
{
    private readonly HumansDbContext _dbContext;

    public EventGuideDashboardController(HumansDbContext dbContext, UserManager<User> userManager)
        : base(userManager)
    {
        _dbContext = dbContext;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var guideSettings = await _dbContext.GuideSettings
            .Include(g => g.EventSettings)
            .FirstOrDefaultAsync();

        DateTimeZone? tz = guideSettings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId)
            : null;

        var allEvents = await _dbContext.GuideEvents
            .Include(e => e.Category)
            .Include(e => e.Camp!).ThenInclude(c => c.Seasons)
            .ToListAsync();

        // Overview counts
        var model = new GuideDashboardViewModel
        {
            TotalCount = allEvents.Count,
            PendingCount = allEvents.Count(e => e.Status == GuideEventStatus.Pending),
            ApprovedCount = allEvents.Count(e => e.Status == GuideEventStatus.Approved),
            RejectedCount = allEvents.Count(e => e.Status == GuideEventStatus.Rejected),
            ResubmitRequestedCount = allEvents.Count(e => e.Status == GuideEventStatus.ResubmitRequested),
            WithdrawnCount = allEvents.Count(e => e.Status == GuideEventStatus.Withdrawn)
        };

        // Coverage by day (approved events only, expand recurring)
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
                var occurrences = GetOccurrenceInstants(e);
                foreach (var occ in occurrences)
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

        // Coverage by category
        var categories = await _dbContext.EventCategories
            .Where(c => c.IsActive)
            .OrderBy(c => c.DisplayOrder)
            .ToListAsync();

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

        // Top submitting camps
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

    private static List<Instant> GetOccurrenceInstants(Domain.Entities.GuideEvent e)
    {
        if (e.IsRecurring && !string.IsNullOrEmpty(e.RecurrenceDays))
        {
            return e.RecurrenceDays
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, CultureInfo.InvariantCulture, out var d) ? (int?)d : null)
                .Where(d => d.HasValue)
                .Select(d => e.StartAt.Plus(Duration.FromDays(d!.Value)))
                .ToList();
        }

        return [e.StartAt];
    }

    private static int ComputeDayOffset(Instant instant, LocalDate gateOpeningDate, DateTimeZone? tz)
    {
        LocalDate eventDate = tz != null
            ? instant.InZone(tz).Date
            : LocalDate.FromDateTime(instant.ToDateTimeUtc());
        return Period.Between(gateOpeningDate, eventDate, PeriodUnits.Days).Days;
    }
}
