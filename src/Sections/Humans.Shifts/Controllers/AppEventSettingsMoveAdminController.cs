using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Shifts.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.Controllers;

/// <summary>
/// Operator screen for #1104: carries the app-wide event values off the Shifts
/// row into Settings. Idempotent and re-runnable; retires with the old columns.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Shifts/Admin/EventSettingsCarry")]
internal sealed class AppEventSettingsMoveAdminController(
    IAppEventSettingsMoveService moveService,
    IUserServiceRead userService,
    ILogger<AppEventSettingsMoveAdminController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        return View(await moveService.GetSnapshotAsync(ct));
    }

    [HttpPost("Run")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(CancellationToken ct = default)
    {
        var carried = await moveService.CarryAsync(ct);
        if (carried == 0)
            SetSuccess("Settings already holds every event row — nothing to carry.");
        else
            SetSuccess($"Carried {carried} event row(s) into Settings.");

        logger.LogInformation("App-wide event settings carry: wrote {Count} row(s) into Settings", carried);
        return RedirectToAction(nameof(Index));
    }
}
