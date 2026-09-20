using Humans.GoogleIntegration.Models;
using Humans.GoogleIntegration.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.GoogleIntegration.ViewComponents;

/// <summary>
/// The /Settings#google-sync tab (peterdrier/Humans#1634): wraps the same form that used
/// to live at /Google/SyncSettings. <see cref="SectionSettings"/> contributes this tab
/// with <c>PolicyNames.AdminOnly</c>, so composition already keeps a non-admin from
/// seeing it — no further check needed here.
/// </summary>
internal sealed class GoogleSyncSettingsTabViewComponent(
    ISyncSettingsService syncSettingsService,
    IUserServiceRead userService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var settings = (await syncSettingsService.GetAllAsync())
            // Sort by the enum's string name to match the prior EF ordering
            // (ServiceType is stored via .HasConversion<string>()).
            .OrderBy(s => s.ServiceType.ToString(), StringComparer.Ordinal)
            .ToList();

        // In-memory join: resolve UpdatedByUser display names via IUserServiceRead
        // rather than an EF .Include across the section boundary (design-rules §6).
        var updatedByUserIds = settings
            .Select(s => s.UpdatedByUserId)
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var updatedByUsers = updatedByUserIds.Count > 0
            ? await userService.GetUserInfosAsync(updatedByUserIds)
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
