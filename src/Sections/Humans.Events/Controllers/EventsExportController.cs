using Humans.Base.Extensions;
using Humans.Camps.Contracts;
using Humans.Events.Services;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Events.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using static Humans.Events.Helpers.EventsLookupHelpers;
using static Humans.Events.Helpers.EventsTimeHelpers;
using Humans.Users.Contracts;

namespace Humans.Events.Controllers;

[Authorize(Policy = PolicyNames.EventsAdminOrAdmin)]
[Route("Events/Export")]
[ServiceFilter(typeof(EventsFeatureFilter))]
internal sealed class EventsExportController(
    IEventManagement guide,
    ICampService camps,
    IUserService userService) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public IActionResult Index() => View();

    [HttpGet("Csv")]
    public async Task<IActionResult> DownloadCsv(CancellationToken ct)
    {
        var actorUserId = GetCurrentUserId();
        if (actorUserId is null) return Unauthorized();

        var bytes = await guide.GetApprovedEventsForExportAsync(actorUserId.Value, ct);
        return File(bytes, "text/csv", "event-guide-export.csv");
    }

    [HttpGet("PrintGuide")]
    public async Task<IActionResult> PrintGuide(CancellationToken ct)
    {
        var events = await guide.GetApprovedEventsAsync(null, null, null, null, [], ct);
        var settings = await guide.GetGuideSettingsAsync(ct);
        var eventSettings = await LoadBurnSettingsAsync(guide, settings, ct);
        var tz = GetTimeZone(eventSettings);
        var maxSlots = settings?.MaxPrintSlots;
        var campsById = await LoadCampsByIdAsync(camps, eventSettings?.GateOpeningDate.Year, ct);

        var gateOpeningDate = eventSettings?.GateOpeningDate;
        var allOccurrences = new List<PrintGuideEntry>();
        foreach (var e in events)
        {
            var camp = e.CampId.HasValue ? campsById.GetValueOrDefault(e.CampId.Value) : null;
            var campName = ResolveCampName(camp);
            var venueName = e.VenueName;

            foreach (var occ in e.GetOccurrenceInstants(gateOpeningDate, tz))
            {
                allOccurrences.Add(new PrintGuideEntry
                {
                    Title = e.Title,
                    Description = e.Description,
                    CategoryName = e.CategoryName,
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
                .OrderBy(o => o.PriorityRank ?? int.MaxValue)
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
                DayLabel = LocalDate.FromDateTime(g.Key).ToWeekdayDayMonth(),
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

    internal sealed class PrintGuideViewModel
    {
        public string EventName { get; set; } = string.Empty;
        public string? TimeZoneId { get; set; }
        public List<PrintGuideDayGroup> DayGroups { get; set; } = [];
    }

    internal sealed class PrintGuideDayGroup
    {
        public string DayLabel { get; set; } = string.Empty;
        public List<PrintGuideEntry> Entries { get; set; } = [];
    }

    internal sealed class PrintGuideEntry
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        public string CampOrVenueName { get; set; } = string.Empty;
        public string? LocationNote { get; set; }
        public DateTime StartAt { get; set; }
        public int DurationMinutes { get; set; }
        public int? PriorityRank { get; set; }
    }
}
