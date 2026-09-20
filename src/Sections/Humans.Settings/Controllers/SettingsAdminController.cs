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

        if (GetCurrentUserId() is not { } actorId) return Challenge();

        try
        {
            await settingsService.SaveEventSettingsAsync(parsed.Settings!, actorId, ct);
        }
        catch (InvalidOperationException ex)
        {
            // The service's own invariant — activating while another cycle is Active. A
            // conflict an operator can act on, so it belongs on the form it came from,
            // not in a 500.
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(model);
        }

        SetSuccess("Event settings saved.");
        // By id, not bare: deactivating the row takes it off the default GET.
        return Redirect($"/Settings?event={parsed.Settings!.Id}#event");
    }
}
