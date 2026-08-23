using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Users.Controllers;

// BurnerName + legal name move from Profile onto User — see nobodies-collective/Humans#1097.
// Review → confirm admin screen; the migration itself moves no data. Idempotent.
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Profile/Admin/NameBackfill")]
internal sealed class UserNameBackfillAdminController(
    IUserService userService,
    IUserNameSyncService nameSyncService,
    ILogger<UserNameBackfillAdminController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var unsynced = await nameSyncService.GetUnsyncedAsync(ct);
        return View(new UserNameBackfillViewModel(unsynced));
    }

    [HttpPost("Run")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(CancellationToken ct = default)
    {
        var synced = await nameSyncService.SyncAllAsync(ct);
        if (synced == 0)
        {
            SetSuccess("Every human already carries their names on the User row — nothing to do.");
            return RedirectToAction(nameof(Index));
        }

        logger.LogInformation("User name backfill: synced {Count} humans", synced);
        SetSuccess($"Copied names onto {synced} User rows.");
        return RedirectToAction(nameof(Index));
    }
}

internal sealed record UserNameBackfillViewModel(IReadOnlyList<UnsyncedNameRow> UnsyncedRows);
