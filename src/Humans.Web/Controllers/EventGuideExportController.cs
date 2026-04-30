using System.Globalization;
using System.Text;
using Humans.Application.Interfaces.EventGuide;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleGroups.GuideModeratorOrAdmin)]
[Route("EventGuide/Export")]
[ServiceFilter(typeof(EventGuideFeatureFilter))]
public class EventGuideExportController : HumansControllerBase
{
    private readonly IEventGuideService _guide;

    public EventGuideExportController(IEventGuideService guide, UserManager<User> userManager)
        : base(userManager)
    {
        _guide = guide;
    }

    [HttpGet("")]
    public IActionResult Index() => View();

    [HttpGet("Csv")]
    public async Task<IActionResult> DownloadCsv()
    {
        var (events, settings) = await _guide.GetApprovedEventsForExportAsync();
        var tz = GetTz(settings);

        var sb = new StringBuilder();
        sb.Append('﻿');
        sb.AppendLine("Id,Title,Description,Category,CampName,VenueName,SubmitterName,LocationNote,Date,StartTime,DurationMinutes,IsRecurring,PriorityRank,Status,SubmittedAt");

        foreach (var e in events)
        {
            var campSeason = e.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
            var campName = campSeason?.Name ?? e.Camp?.Slug ?? "";
            var venueName = e.GuideSharedVenue?.Name ?? "";
            var submitterName = e.CampId == null
                ? (e.SubmitterUser.Profile?.BurnerName ?? e.SubmitterUser.GetEffectiveEmail() ?? "")
                : "";

            foreach (var (date, time) in GetOccurrences(e, tz))
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
                    CsvEscape(ToLocal(e.SubmittedAt, tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                ));
            }
        }

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "event-guide-export.csv");
    }

    [HttpGet("PrintGuide")]
    public async Task<IActionResult> PrintGuide()
    {
        var (events, settings) = await _guide.GetApprovedEventsForExportAsync();
        var tz = GetTz(settings);
        var maxSlots = settings?.MaxPrintSlots;

        var allOccurrences = new List<PrintGuideEntry>();
        foreach (var e in events)
        {
            var campSeason = e.Camp?.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
            var campName = campSeason?.Name ?? e.Camp?.Slug;
            var venueName = e.GuideSharedVenue?.Name;

            foreach (var occ in GetOccurrenceInstants(e))
            {
                allOccurrences.Add(new PrintGuideEntry
                {
                    Title = e.Title,
                    Description = e.Description,
                    CategoryName = e.Category.Name,
                    CampOrVenueName = campName ?? venueName ?? "",
                    LocationNote = e.LocationNote,
                    StartAt = ToLocal(occ, tz),
                    DurationMinutes = e.DurationMinutes,
                    PriorityRank = e.PriorityRank
                });
            }
        }

        if (maxSlots.HasValue && maxSlots.Value > 0)
        {
            allOccurrences = allOccurrences
                .OrderBy(o => o.PriorityRank == 0 ? int.MaxValue : o.PriorityRank)
                .ThenBy(o => o.StartAt)
                .Take(maxSlots.Value)
                .ToList();
        }

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

        var model = new PrintGuideViewModel
        {
            EventName = settings?.EventSettings.EventName ?? "Event Guide",
            TimeZoneId = settings?.EventSettings.TimeZoneId,
            DayGroups = dayGroups
        };

        return View(model);
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private static DateTimeZone? GetTz(GuideSettings? settings)
        => settings?.EventSettings != null
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(settings.EventSettings.TimeZoneId)
            : null;

    private static DateTime ToLocal(Instant instant, DateTimeZone? tz)
        => tz == null ? instant.ToDateTimeUtc() : instant.InZone(tz).ToDateTimeUnspecified();

    private static List<(string Date, string Time)> GetOccurrences(GuideEvent e, DateTimeZone? tz)
    {
        var results = new List<(string, string)>();
        if (e.IsRecurring && !string.IsNullOrEmpty(e.RecurrenceDays))
        {
            foreach (var offsetStr in e.RecurrenceDays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(offsetStr, CultureInfo.InvariantCulture, out var d)) continue;
                var local = ToLocal(e.StartAt.Plus(Duration.FromDays(d)), tz);
                results.Add((local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), local.ToString("HH:mm", CultureInfo.InvariantCulture)));
            }
        }
        else
        {
            var local = ToLocal(e.StartAt, tz);
            results.Add((local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), local.ToString("HH:mm", CultureInfo.InvariantCulture)));
        }
        return results;
    }

    private static List<Instant> GetOccurrenceInstants(GuideEvent e)
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
