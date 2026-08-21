using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.GoogleIntegration.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.GoogleIntegration.Controllers;

// One-time audit_log → google_sync_log history move (nobodies-collective/Humans#1083).
// GET is the dry run; POST copies. Source rows are never touched, and a re-run moves
// nothing because the copy keeps the audit row's id. Remove with the six audit columns.
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Google/Admin/SyncHistoryMigration")]
internal sealed class GoogleSyncHistoryMigrationAdminController(
    IUserServiceRead users,
    IGoogleSyncHistoryMigrationService migration) : HumansControllerBase(users)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var report = await migration.PreviewAsync(ct);
        return View(new GoogleSyncHistoryMigrationViewModel(report));
    }

    [HttpPost("Run")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        // Recomputed server-side: nothing the page posted decides what moves.
        var report = await migration.MigrateAsync(ct);

        if (report.Moved == 0)
            SetSuccess("Nothing to move — every mappable row is already in google_sync_log.");
        else
            SetSuccess($"Copied {report.Moved} row(s) into google_sync_log. " +
                       $"{report.AlreadyPresent} were already there, {report.Skipped} could not be mapped.");

        return RedirectToAction(nameof(Index));
    }
}

internal sealed record GoogleSyncHistoryMigrationViewModel(GoogleSyncHistoryMigrationReport Report);
