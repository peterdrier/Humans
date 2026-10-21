using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Debug.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Debug.Controllers;

/// <summary>
/// <c>/Debug/DataMigrations</c>: every section's registered data migration with its pending
/// rows, and the Apply button that runs one (memory/process/no-data-backfills.md's
/// review → confirm screen). The anonymous counts-only twin is <c>/api/data-migrations</c>,
/// mapped by <see cref="SectionEndpoints"/>.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Debug/DataMigrations")]
internal sealed class DataMigrationsController(
    IUserService userService,
    DataMigrationService dataMigrations) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(await dataMigrations.GetReportsAsync(ct));

    [HttpPost("Apply")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply(string key, CancellationToken ct)
    {
        var actorId = GetCurrentUserId();
        if (actorId is null)
            return Unauthorized();

        var rows = await dataMigrations.ApplyAsync(key, actorId.Value, ct);
        if (rows is null)
            return NotFound();

        SetSuccess($"{key}: {rows} row(s) written.");
        return RedirectToAction(nameof(Index));
    }
}
