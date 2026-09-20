using Humans.Events.Models;
using Humans.Events.Services;
using Microsoft.AspNetCore.Mvc;
using static Humans.Events.Helpers.EventsTimeHelpers;

namespace Humans.Events.ViewComponents;

/// <summary>
/// The /Settings#event-guide tab (peterdrier/Humans#1634): submission window, publish
/// time, print slots. <see cref="SectionSettings"/> gates it on
/// <c>PolicyNames.EventsAdminOrAdmin</c> — the same policy the old
/// <c>Events/Admin/Settings</c> page used, so every viewer who reaches this component
/// may edit. Mirrors <c>EventsAdminController.Settings</c>'s former data assembly.
/// </summary>
internal sealed class EventGuideSettingsTabViewComponent(IEventService guide) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var existing = await guide.GetGuideSettingsAsync();
        var eventSettingsOptions = await BuildEventSettingsOptionsAsync();

        if (existing == null)
        {
            return View(new GuideSettingsViewModel
            {
                AvailableEventSettings = eventSettingsOptions,
                MaxPrintSlots = 100
            });
        }

        var eventSettings = await guide.GetEventSettingsByIdAsync(existing.EventSettingsId);
        var tz = GetTimeZone(eventSettings);
        return View(new GuideSettingsViewModel
        {
            Id = existing.Id,
            EventSettingsId = existing.EventSettingsId,
            SubmissionOpenAt = ToLocalDateTime(existing.SubmissionOpenAt, tz),
            SubmissionCloseAt = ToLocalDateTime(existing.SubmissionCloseAt, tz),
            GuidePublishAt = ToLocalDateTime(existing.GuidePublishAt, tz),
            MaxPrintSlots = existing.MaxPrintSlots,
            AvailableEventSettings = eventSettingsOptions,
            TimeZoneId = eventSettings?.TimeZoneId
        });
    }

    private async Task<List<EventSettingsOptionViewModel>> BuildEventSettingsOptionsAsync()
    {
        var options = await guide.GetEventSettingsOptionsAsync();
        return options.Select(e => new EventSettingsOptionViewModel { Id = e.Id, EventName = e.EventName }).ToList();
    }
}
