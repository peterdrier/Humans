using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Settings.Contracts;
using Humans.Settings.Models;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Settings.Controllers;

/// <summary>
/// The app-wide event settings screen (#1104). Lives at <c>/Settings/Admin</c>,
/// not <c>/Admin/Settings</c> — top-level <c>/Admin/*</c> is frozen
/// (memory/architecture/no-admin-url-section.md).
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Settings/Admin")]
internal sealed class SettingsAdminController(
    ISettingsService settingsService,
    IUserServiceRead userService) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var active = await settingsService.GetActiveEventSettingsAsync(ct);
        return View(active is null
            ? new AppEventSettingsViewModel { IsActive = true }
            : AppEventSettingsFormMapper.ToViewModel(active));
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(AppEventSettingsViewModel model, CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
            return View(model);

        var parsed = AppEventSettingsFormMapper.Parse(model);
        if (!parsed.Success)
        {
            foreach (var error in parsed.Errors)
                ModelState.AddModelError(error.FieldName, error.Message);

            return View(model);
        }

        await settingsService.SaveEventSettingsAsync(parsed.Settings!, ct);
        SetSuccess("Event settings saved.");
        return RedirectToAction(nameof(Index));
    }
}
