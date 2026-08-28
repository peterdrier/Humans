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
/// Takes <see cref="ISettingsWriteService"/>, not <c>ISettingsService</c>: the
/// event-settings write is deliberately off the cross-section contract, so the
/// only callers that can reach it are the section's own screens.
/// </remarks>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Settings/Admin")]
internal sealed class SettingsAdminController(
    ISettingsWriteService settingsService,
    IUserServiceRead userService) : HumansControllerBase(userService)
{
    /// <summary>
    /// Edits one row: the active one by default, or <paramref name="id"/> when the
    /// caller names it. Inactive rows stay reachable that way — the carry screen
    /// links every carried row here, and a save redirects back with its own id.
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(Guid? id, CancellationToken ct = default)
    {
        var settings = id is null
            ? await settingsService.GetActiveEventSettingsAsync(ct)
            : await settingsService.GetEventSettingsByIdAsync(id.Value, ct);

        // No blank creatable form: event ids belong to the Shifts rows until the
        // old columns are dropped, so a row is born by the carry, never here.
        return settings is null
            ? View("NoEvent")
            : View(EventSettingsFormMapper.ToViewModel(settings));
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

        try
        {
            await settingsService.SaveEventSettingsAsync(parsed.Settings!, ct);
        }
        catch (InvalidOperationException ex)
        {
            // The service's own invariants — activating while another cycle is Active, or an id
            // no Shifts event row carries. Both are conflicts an operator can act on, so they
            // belong on the form they came from, not in a 500.
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(model);
        }

        SetSuccess("Event settings saved.");
        // By id, not bare: deactivating the row takes it off the default GET.
        return RedirectToAction(nameof(Index), new { id = parsed.Settings!.Id });
    }
}
