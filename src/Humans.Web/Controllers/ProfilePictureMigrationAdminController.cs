using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Profiles;
using Humans.Domain.Entities;
using Humans.Web.Authorization;

namespace Humans.Web.Controllers;

/// <summary>
/// Issue nobodies-collective/Humans#702 — Profile-picture DB→FS migration
/// verification admin tool.
/// </summary>
/// <remarks>
/// Sits between Phase 1 (#527, shipped — filesystem store with DB fallback +
/// migrate-on-read + dual-write) and Phase 2 (#528 — drop
/// <c>Profile.ProfilePictureData</c>). Confirms in QA/prod that every
/// DB-stored picture is also on disk, so #528 can't lose data. The
/// <c>Run</c> POST drives the existing migrate-on-read path
/// (<see cref="IProfileService.GetProfilePictureAsync"/>) which writes
/// through <see cref="Humans.Application.Interfaces.IFileStorage"/> on a DB
/// hit — making this page a thin admin trigger over already-shipped
/// behavior. Idempotent.
/// <para>
/// Routed at <c>/Profile/Admin/PictureMigration</c> per
/// <c>memory/architecture/no-admin-url-section.md</c> (admin pages live
/// under <c>/&lt;Section&gt;/Admin/*</c>, never <c>/Admin/&lt;Section&gt;/*</c>).
/// </para>
/// </remarks>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Profile/Admin/PictureMigration")]
public sealed class ProfilePictureMigrationAdminController : HumansControllerBase
{
    private readonly IProfileService _profileService;
    private readonly ILogger<ProfilePictureMigrationAdminController> _logger;

    public ProfilePictureMigrationAdminController(
        UserManager<User> userManager,
        IProfileService profileService,
        ILogger<ProfilePictureMigrationAdminController> logger)
        : base(userManager)
    {
        _profileService = profileService;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var snapshot = await _profileService.GetProfilePictureMigrationSnapshotAsync(ct);
        return View(new ProfilePictureMigrationViewModel(snapshot));
    }

    [HttpPost("Run")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(CancellationToken ct = default)
    {
        var snapshot = await _profileService.GetProfilePictureMigrationSnapshotAsync(ct);
        if (snapshot.DbOnlyCount == 0)
        {
            SetSuccess("All DB-stored profile pictures are already on the filesystem — nothing to migrate.");
            return RedirectToAction(nameof(Index));
        }

        // GetProfilePictureAsync drives migrate-on-read: it reads bytes from
        // the DB fallback and writes them through IFileStorage. We discard
        // the returned tuple — the side effect (FS save) is the whole point.
        var migrated = 0;
        foreach (var row in snapshot.DbOnlyRows)
        {
            var result = await _profileService.GetProfilePictureAsync(row.ProfileId, ct);
            if (result is not null)
            {
                migrated++;
            }
        }

        _logger.LogInformation(
            "Profile-picture DB→FS migration: drove migrate-on-read for {Count} profiles", migrated);
        SetSuccess($"Migrated {migrated} profile picture(s) from DB to filesystem.");
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>
/// Issue nobodies-collective/Humans#702: view model for the profile-picture
/// DB→FS migration verification page.
/// </summary>
public sealed record ProfilePictureMigrationViewModel(ProfilePictureMigrationSnapshot Snapshot);
