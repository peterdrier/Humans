using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Settings.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Settings.Controllers;

/// <summary>
/// Operator screen for nobodies-collective/Humans#1104: copies the app-wide event
/// values off the Shifts-owned rows into <c>settings_event</c>. Idempotent and
/// re-runnable; retires once the Shifts columns are dropped.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Settings/Admin/Carry")]
internal sealed class EventSettingsCarryAdminController(
    IEventSettingsCarryService carryService,
    IUserServiceRead userService,
    ILogger<EventSettingsCarryAdminController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct = default) =>
        View(await carryService.GetSnapshotAsync(ct));

    [HttpPost("Run")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(CancellationToken ct = default)
    {
        var carried = await carryService.CarryAsync(ct);
        if (carried == 0)
            SetSuccess("Every event row is already here and in step — nothing to do.");
        else
            SetSuccess($"Carried or reconciled {carried} event row(s) in Settings.");

        logger.LogInformation("Event settings carry: wrote {Count} row(s) into settings_event", carried);
        return RedirectToAction(nameof(Index));
    }
}
