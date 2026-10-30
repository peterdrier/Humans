using Humans.Events.Models;
using Humans.Events.Services;
using Microsoft.AspNetCore.Mvc;
using static Humans.Events.Helpers.EventsTimeHelpers;

namespace Humans.Events.ViewComponents;

/// <summary>Every viewer who reaches this tab may edit: it is gated on <c>EventsPolicies.Manage</c>.</summary>
internal sealed class EventGuideSettingsTabViewComponent(IEventManagement guide) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = ViewComponentContext.ViewContext?.HttpContext?.RequestAborted ?? default;
        var existing = await guide.GetGuideSettingsAsync(ct);
        var eventSettingsOptions = await BuildEventSettingsOptionsAsync(ct);

        if (existing == null)
        {
            return View(new GuideSettingsViewModel
            {
                AvailableEventSettings = eventSettingsOptions,
                MaxPrintSlots = 100
            });
        }

        var eventSettings = await guide.GetEventSettingsByIdAsync(existing.EventSettingsId, ct);
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

    private async Task<List<EventSettingsOptionViewModel>> BuildEventSettingsOptionsAsync(CancellationToken ct)
    {
        var options = await guide.GetEventSettingsOptionsAsync(ct);
        return EventSettingsOptionViewModel.From(options);
    }
}
