using System.Globalization;
using System.Text;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Extensions;
using Humans.Web.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using static Humans.Web.Helpers.EventsLookupHelpers;
using static Humans.Web.Helpers.EventsTimeHelpers;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleGroups.EventsAdminOrAdmin)]
[Route("Events/Export")]
[ServiceFilter(typeof(EventsFeatureFilter))]
public class EventsExportController(
    IEventService guide,
    ICampServiceRead camps,
    IUserServiceRead users,
    IUserServiceRead userService) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public IActionResult Index() => View();

    [HttpGet("Csv")]
    public async Task<IActionResult> DownloadCsv()
    {
        var (events, settings) = await guide.GetApprovedEventsForExportAsync();
        var eventSettings = settings != null
            ? await guide.GetEventSettingsByIdAsync(settings.EventSettingsId)
            : null;
        var tz = GetTimeZone(eventSettings);
        var campsById = await LoadCampsByIdAsync(camps, eventSettings?.GateOpeningDate.Year);

        var sb = new StringBuilder();
        sb.Append('﻿');
        sb.AppendLine("Id,Title,Description,Category,CampName,VenueName,SubmitterName,LocationNote,Date,StartTime,DurationMinutes,IsRecurring,PriorityRank,Status,SubmittedAt");

        foreach (var e in events)
        {
            var camp = e.CampId.HasValue ? campsById.GetValueOrDefault(e.CampId.Value) : null;
            var seasonName = camp?.Active?.Name;
            var campName = seasonName ?? camp?.Slug ?? "";
            var venueName = e.EventVenue?.Name ?? "";
            var submitterName = "";
            if (e.CampId == null)
            {
                var submitter = await users.GetUserInfoAsync(e.SubmitterUserId);
                submitterName = submitter?.BurnerName ?? "";
            }

            foreach (var (date, time) in GetOccurrences(e, eventSettings?.GateOpeningDate, tz))
            {
                sb.AppendCsvRow(
                    e.Id.ToString(),
                    e.Title,
                    e.Description,
                    e.Category.Name,
                    campName,
                    venueName,
                    submitterName,
                    e.LocationNote ?? "",
                    date,
                    time,
                    e.DurationMinutes,
                    e.IsRecurring ? "Yes" : "No",
                    e.PriorityRank,
                    e.Status.ToString(),
                    ToLocalDateTime(e.SubmittedAt, tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            }
        }

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "event-guide-export.csv");
    }

    [HttpGet("PrintGuide")]
    public async Task<IActionResult> PrintGuide()
    {
        var (events, settings) = await guide.GetApprovedEventsForExportAsync();
        var eventSettings = settings != null
            ? await guide.GetEventSettingsByIdAsync(settings.EventSettingsId)
            : null;
        var tz = GetTimeZone(eventSettings);
        var maxSlots = settings?.MaxPrintSlots;
        var campsById = await LoadCampsByIdAsync(camps, eventSettings?.GateOpeningDate.Year);

        var gateOpeningDate = eventSettings?.GateOpeningDate;
        var allOccurrences = new List<PrintGuideEntry>();
        foreach (var e in events)
        {
            var camp = e.CampId.HasValue ? campsById.GetValueOrDefault(e.CampId.Value) : null;
            var seasonName = camp?.Active?.Name;
            var campName = seasonName ?? camp?.Slug;
            var venueName = e.EventVenue?.Name;

            foreach (var occ in gateOpeningDate.HasValue && tz != null ? e.GetOccurrenceInstants(gateOpeningDate.Value, tz) : (IReadOnlyList<Instant>)[e.StartAt])
            {
                allOccurrences.Add(new PrintGuideEntry
                {
                    Title = e.Title,
                    Description = e.Description,
                    CategoryName = e.Category.Name,
                    CampOrVenueName = campName ?? venueName ?? "",
                    LocationNote = e.LocationNote,
                    StartAt = ToLocalDateTime(occ, tz),
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
            EventName = eventSettings?.EventName ?? "Event Guide",
            TimeZoneId = eventSettings?.TimeZoneId,
            DayGroups = dayGroups
        };

        return View(model);
    }

    private static List<(string Date, string Time)> GetOccurrences(Event e, LocalDate? gateOpeningDate, DateTimeZone? tz)
    {
        var results = new List<(string, string)>();
        foreach (var occurrence in gateOpeningDate.HasValue && tz != null ? e.GetOccurrenceInstants(gateOpeningDate.Value, tz) : (IReadOnlyList<Instant>)[e.StartAt])
        {
            var local = ToLocalDateTime(occurrence, tz);
            results.Add((local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), local.ToString("HH:mm", CultureInfo.InvariantCulture)));
        }
        return results;
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
