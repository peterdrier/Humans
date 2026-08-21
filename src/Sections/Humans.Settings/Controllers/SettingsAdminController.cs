using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Settings.Models;
using Humans.Settings.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Settings.Controllers;

/// <summary>
/// The app-wide event settings screen (#1104). Lives at <c>/Settings/Admin</c>,
/// not <c>/Admin/Settings</c> — top-level <c>/Admin/*</c> is frozen
/// (memory/architecture/no-admin-url-section.md).
/// </summary>
/// <remarks>
/// Takes the concrete <see cref="Service"/>, not <c>ISettingsService</c>: the
/// event-settings write is deliberately off the cross-section contract, so the
/// only callers that can reach it are the section's own screens.
/// </remarks>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Settings/Admin")]
internal sealed class SettingsAdminController(
    Service settingsService,
    IUserServiceRead userService) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var active = await settingsService.GetActiveEventSettingsAsync(ct);
        return View(active is null
            ? new EventSettingsViewModel { IsActive = true }
            : EventSettingsFormMapper.ToViewModel(active));
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(EventSettingsViewModel model, CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
            return View(model);

        var parsed = EventSettingsFormMapper.Parse(model);
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
