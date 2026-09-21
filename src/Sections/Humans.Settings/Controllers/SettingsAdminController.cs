using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Settings.Models;
using Humans.Settings.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Settings.Controllers;

/// <summary>
/// The app-wide event settings POST endpoint (nobodies-collective/Humans#1104).
/// Lives at <c>/Settings/Admin</c>,
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
        {
            return BackToTab(model.Id, Describe(
                ModelState.Values.SelectMany(state => state.Errors).Select(error => error.ErrorMessage)));
        }

        var parsed = EventSettingsFormMapper.Parse(model);
        if (!parsed.Success)
            return BackToTab(model.Id, Describe(parsed.Errors.Select(error => error.Message)));

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
            return BackToTab(model.Id, ex.Message);
        }

        SetSuccess("Event settings saved.");
        // By id, not bare: the /Settings#event tab defaults to the active row, so a
        // row just deactivated is only reachable by naming its id.
        return Redirect($"/Settings?event={parsed.Settings!.Id}#event");
    }

    /// <summary>
    /// Post-redirect-get back to the Event tab, the way every other settings tab's POST
    /// ends. There is no GET here to re-render, so the failing rule travels as a flash.
    /// </summary>
    private IActionResult BackToTab(Guid? id, string message)
    {
        SetError(message);
        return Redirect(id is { } eventId ? $"/Settings?event={eventId}#event" : "/Settings#event");
    }

    private static string Describe(IEnumerable<string> messages)
    {
        var joined = string.Join(" ", messages.Where(message => !string.IsNullOrWhiteSpace(message)));
        return joined.Length > 0 ? joined : "Invalid event settings.";
    }
}
