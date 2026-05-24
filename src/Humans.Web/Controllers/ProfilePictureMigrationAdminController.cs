using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Profiles;
using Humans.Web.Authorization;

using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

// Profile-picture DB→FS migration verification — see #702. Idempotent; drives migrate-on-read.
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Profile/Admin/PictureMigration")]
public sealed class ProfilePictureMigrationAdminController(
    IUserServiceRead userService,
    IProfilePictureService profilePictureService,
    ILogger<ProfilePictureMigrationAdminController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var snapshot = await profilePictureService.GetProfilePictureMigrationSnapshotAsync(ct);
        return View(new ProfilePictureMigrationViewModel(snapshot));
    }

    [HttpPost("Run")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(CancellationToken ct = default)
    {
        var snapshot = await profilePictureService.GetProfilePictureMigrationSnapshotAsync(ct);
        if (snapshot.DbOnlyCount == 0)
        {
            SetSuccess("All DB-stored profile pictures are already on the filesystem — nothing to migrate.");
            return RedirectToAction(nameof(Index));
        }

        // GetProfilePictureAsync's FS-save side effect IS the migration; result tuple discarded.
        var migrated = 0;
        foreach (var row in snapshot.DbOnlyRows)
        {
            var result = await profilePictureService.GetProfilePictureAsync(row.ProfileId, ct);
            if (result is not null)
            {
                migrated++;
            }
            else
            {
                logger.LogWarning(
                    "Profile-picture migration: GetProfilePictureAsync returned null for DB-only profile {ProfileId} (userId {UserId}); row skipped",
                    row.ProfileId, row.UserId);
            }
        }

        logger.LogInformation(
            "Profile-picture DB→FS migration: drove migrate-on-read for {Count} profiles", migrated);
        SetSuccess($"Migrated {migrated} profile picture(s) from DB to filesystem.");
        return RedirectToAction(nameof(Index));
    }
}

public sealed record ProfilePictureMigrationViewModel(ProfilePictureMigrationSnapshot Snapshot);
