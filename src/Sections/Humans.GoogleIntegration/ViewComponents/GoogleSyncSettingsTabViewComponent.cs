using Humans.GoogleIntegration.Models;
using Humans.GoogleIntegration.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.GoogleIntegration.ViewComponents;

/// <summary>No role check here: <see cref="SectionSettings"/> contributes the tab as AdminOnly.</summary>
internal sealed class GoogleSyncSettingsTabViewComponent(
    ISyncSettingsService syncSettingsService,
    IUserService userService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = ViewComponentContext.ViewContext?.HttpContext?.RequestAborted ?? default;
        var settings = (await syncSettingsService.GetAllAsync(ct))
            // By name, not enum value: ServiceType is stored as a string.
            .OrderBy(s => s.ServiceType.ToString(), StringComparer.Ordinal)
            .ToList();

        var updatedByUserIds = settings
            .Select(s => s.UpdatedByUserId)
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var updatedByUsers = updatedByUserIds.Count > 0
            ? await userService.GetUserInfosAsync(updatedByUserIds, ct)
            : new Dictionary<Guid, UserInfo>();

        var viewModel = new SyncSettingsViewModel
        {
            Settings = settings.Select(s => new SyncServiceSettingViewModel
            {
                ServiceType = s.ServiceType,
                ServiceName = SyncServiceNameFormatter.Format(s.ServiceType),
                CurrentMode = s.SyncMode,
                UpdatedAt = s.UpdatedAt.ToDateTimeUtc(),
                UpdatedByName = s.UpdatedByUserId is { } uid && updatedByUsers.TryGetValue(uid, out var u)
                    ? u.BurnerName
                    : null
            }).ToList()
        };

        return View(viewModel);
    }
}
