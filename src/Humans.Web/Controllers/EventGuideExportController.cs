using System.Globalization;
using System.Text;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Web.Filters;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleGroups.GuideModeratorOrAdmin)]
[Route("EventGuide/Export")]
[ServiceFilter(typeof(EventGuideFeatureFilter))]
public class EventGuideExportController : HumansControllerBase
{
    private readonly HumansDbContext _dbContext;

    public EventGuideExportController(HumansDbContext dbContext, UserManager<User> userManager)
        : base(userManager)
    {
        _dbContext = dbContext;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        return View();
    }

    [HttpGet("Csv")]
    public async Task<IActionResult> DownloadCsv()
    {
        var (events, tz, _) = await LoadApprovedEventsAsync();

        var sb = new StringBuilder();
        // UTF-8 BOM for Excel
        sb.Append('\uFEFF');
        sb.AppendLine("Id,Title,Description,Category,CampName,VenueName,SubmitterName,LocationNote,Date,StartTime,DurationMinutes,IsRecurring,PriorityRank,Status,SubmittedAt");

        foreach (var e in events)
        {
            var campSeason = e.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
            var campName = campSeason?.Name ?? e.Camp?.Slug ?? "";
            var venueName = e.GuideSharedVenue?.Name ?? "";
            var submitterName = e.CampId == null
                ? (e.SubmitterUser.Profile?.BurnerName ?? e.SubmitterUser.Email ?? "")
                : "";

            var occurrences = GetOccurrences(e, tz);
            foreach (var (date, time) in occurrences)
            {
                sb.AppendLine(string.Join(",",
                    CsvEscape(e.Id.ToString()),
                    CsvEscape(e.Title),
                    CsvEscape(e.Description),
                    CsvEscape(e.Category.Name),
                    CsvEscape(campName),
                    CsvEscape(venueName),
                    CsvEscape(submitterName),
                    CsvEscape(e.LocationNote ?? ""),
                    CsvEscape(date),
                    CsvEscape(time),
                    e.DurationMinutes.ToString(CultureInfo.InvariantCulture),
                    e.IsRecurring ? "Yes" : "No",
                    e.PriorityRank.ToString(CultureInfo.InvariantCulture),
                    e.Status.ToString(),
                    CsvEscape(ToLocalDateTime(e.SubmittedAt, tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                ));
            }
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/csv", "event-guide-export.csv");
    }

    [HttpGet("PrintGuide")]
    public async Task<IActionResult> PrintGuide()
    {
        var (events, tz, guideSettings) = await LoadApprovedEventsAsync();

        // Apply MaxPrintSlots limit by priority rank
        var maxSlots = guideSettings?.MaxPrintSlots;

        // Expand all occurrences
        var allOccurrences = new List<PrintGuideEntry>();
        foreach (var e in events)
        {
            var campSeason = e.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
            var campName = campSeason?.Name ?? e.Camp?.Slug;
            var venueName = e.GuideSharedVenue?.Name;

            var occurrences = GetOccurrenceInstants(e);
            foreach (var occurrenceStart in occurrences)
            {
                allOccurrences.Add(new PrintGuideEntry
                {
                    Title = e.Title,
                    Description = e.Description,
                    CategoryName = e.Category.Name,
                    CampOrVenueName = campName ?? venueName ?? "",
                    LocationNote = e.LocationNote,
                    StartAt = ToLocalDateTime(occurrenceStart, tz),
                    DurationMinutes = e.DurationMinutes,
                    PriorityRank = e.PriorityRank
                });
            }
        }

        // Sort by priority rank (lower = higher priority), then by submission order
        if (maxSlots.HasValue && maxSlots.Value > 0)
        {
            allOccurrences = allOccurrences
                .OrderBy(o => o.PriorityRank == 0 ? int.MaxValue : o.PriorityRank)
                .ThenBy(o => o.StartAt)
                .Take(maxSlots.Value)
                .ToList();
        }

        // Group by day, sort by time within each day
        var dayGroups = allOccurrences
            .OrderBy(o => o.StartAt)
            .GroupBy(o => o.StartAt.Date)
            .OrderBy(g => g.Key)
            .Select(g => new PrintGuideDayGroup
            {
                DayLabel = g.Key.ToString("dddd d MMMM", CultureInfo.InvariantCulture),
                Entries = g.OrderBy(e => e.StartAt).ToList()
            })
            .ToList();

        var eventName = guideSettings?.EventSettings.EventName ?? "Event Guide";

        var model = new PrintGuideViewModel
        {
            EventName = eventName,
            TimeZoneId = guideSettings?.EventSettings.TimeZoneId,
            DayGroups = dayGroups
        };

        return View(model);
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private async Task<(List<Domain.Entities.GuideEvent> Events, DateTimeZone? Tz, Domain.Entities.GuideSettings? Settings)> LoadApprovedEventsAsync()
    {
        var guideSettings = await _dbContext.GuideSettings
            .Include(g => g.EventSettings)
            .FirstOrDefaultAsync();

        DateTimeZone? tz = guideSettings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(guideSettings.EventSettings.TimeZoneId)
            : null;

        var events = await _dbContext.GuideEvents
            .Include(e => e.Category)
            .Include(e => e.Camp!).ThenInclude(c => c.Seasons)
            .Include(e => e.GuideSharedVenue)
            .Include(e => e.SubmitterUser).ThenInclude(u => u.Profile)
            .Where(e => e.Status == GuideEventStatus.Approved)
            .OrderBy(e => e.StartAt)
            .ToListAsync();

        return (events, tz, guideSettings);
    }

    private static List<(string Date, string Time)> GetOccurrences(Domain.Entities.GuideEvent e, DateTimeZone? tz)
    {
        var results = new List<(string Date, string Time)>();

        if (e.IsRecurring && !string.IsNullOrEmpty(e.RecurrenceDays))
        {
            var offsets = e.RecurrenceDays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var offsetStr in offsets)
            {
                if (!int.TryParse(offsetStr, CultureInfo.InvariantCulture, out var dayOffset)) continue;
                var occurrenceStart = e.StartAt.Plus(Duration.FromDays(dayOffset));
                var local = ToLocalDateTime(occurrenceStart, tz);
                results.Add((local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), local.ToString("HH:mm", CultureInfo.InvariantCulture)));
            }
        }
        else
        {
            var local = ToLocalDateTime(e.StartAt, tz);
            results.Add((local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), local.ToString("HH:mm", CultureInfo.InvariantCulture)));
        }

        return results;
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

    private static string CsvEscape(string value)
    {
        if (value.Contains('"', StringComparison.Ordinal) || value.Contains(',', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal))
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        return value;
    }

    private static DateTime ToLocalDateTime(Instant instant, DateTimeZone? tz)
    {
        if (tz == null)
            return instant.ToDateTimeUtc();
        return instant.InZone(tz).ToDateTimeUnspecified();
    }

    // View models (inner classes for this controller only)
    public sealed class PrintGuideViewModel
    {
        public string EventName { get; set; } = string.Empty;
        public string? TimeZoneId { get; set; }
        public List<PrintGuideDayGroup> DayGroups { get; set; } = [];
    }

    public sealed class PrintGuideDayGroup
    {
        public string DayLabel { get; set; } = string.Empty;
        public List<PrintGuideEntry> Entries { get; set; } = [];
    }

    public sealed class PrintGuideEntry
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        public string CampOrVenueName { get; set; } = string.Empty;
        public string? LocationNote { get; set; }
        public DateTime StartAt { get; set; }
        public int DurationMinutes { get; set; }
        public int PriorityRank { get; set; }
    }
}
