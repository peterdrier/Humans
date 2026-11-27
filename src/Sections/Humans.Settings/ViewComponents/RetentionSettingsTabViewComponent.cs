using Humans.Base.Interfaces;
using Humans.Settings.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Settings.ViewComponents;

/// <summary>No admin check here: the tab is contributed under <c>PolicyNames.AdminOnly</c>, so non-admins never get it.</summary>
internal sealed class RetentionSettingsTabViewComponent(
    ISettingsWriteService settingsService,
    IEnumerable<IRetentionPeriodContributor> contributors) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var days = RetentionPeriod.Parse(
            await settingsService.GetValueAsync(RetentionPeriod.Key, HttpContext.RequestAborted));
        return View(new RetentionSettingsTabViewModel(days,
            contributors.Select(c => c.Label).Order(StringComparer.Ordinal).ToList()));
    }
}

internal sealed record RetentionSettingsTabViewModel(int Days, IReadOnlyList<string> AppliesTo);
