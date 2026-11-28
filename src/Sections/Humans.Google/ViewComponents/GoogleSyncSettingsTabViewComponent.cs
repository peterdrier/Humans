using Humans.Google.Models;
using Humans.Google.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Google.ViewComponents;

/// <summary>No role check here: <see cref="Section"/> contributes the tab as AdminOnly.</summary>
internal sealed class GoogleSyncSettingsTabViewComponent(
    ISyncSettingsService syncSettingsService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = ViewComponentContext.ViewContext?.HttpContext?.RequestAborted ?? default;
        var settings = (await syncSettingsService.GetAllAsync(ct))
            // By name, not enum value: ServiceType is stored as a string.
            .OrderBy(s => s.ServiceType.ToString(), StringComparer.Ordinal)
            .ToList();

        var viewModel = settings.Select(s => new SyncServiceSettingViewModel
        {
            ServiceType = s.ServiceType,
            ServiceName = SyncServiceNameFormatter.Format(s.ServiceType),
            CurrentMode = s.SyncMode,
            UpdatedAt = s.UpdatedAt.ToDateTimeUtc(),
            UpdatedByUserId = s.UpdatedByUserId,
        }).ToList();

        return View(viewModel);
    }
}
