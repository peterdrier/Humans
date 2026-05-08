using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using NodaTime;

namespace Humans.Web.Controllers;

/// <summary>
/// Issue #635 (§15i) — Stub Profile backfill admin tool.
/// </summary>
/// <remarks>
/// One-shot operator-run page that materializes a <see cref="ProfileState.Stub"/>
/// row for every <see cref="User"/> that does not yet have a Profile. Used
/// once after the §15i deploy to bring legacy profile-less rows (contact
/// imports, pre-Stub-invariant signups) into the new "every User has a
/// Profile" invariant.
/// <para>
/// Idempotent: re-running with N=0 is a no-op; re-running with N&gt;0
/// processes any new gaps that have appeared since the previous run.
/// </para>
/// <para>
/// Routed at <c>/Profile/Admin/Backfill</c> per
/// <c>memory/architecture/no-admin-url-section.md</c> (admin pages live
/// under <c>/&lt;Section&gt;/Admin/*</c>, never <c>/Admin/&lt;Section&gt;/*</c>).
/// The spec body of issue #635 said <c>/Admin/ProfileBackfill</c>; the
/// project rule overrides.
/// </para>
/// </remarks>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Profile/Admin/Backfill")]
public sealed class ProfileBackfillAdminController : HumansControllerBase
{
    private readonly IUserService _userService;
    private readonly IProfileService _profileService;
    private readonly ILogger<ProfileBackfillAdminController> _logger;

    public ProfileBackfillAdminController(
        UserManager<User> userManager,
        IUserService userService,
        IProfileService profileService,
        ILogger<ProfileBackfillAdminController> logger)
        : base(userManager)
    {
        _userService = userService;
        _profileService = profileService;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var missing = await GetUsersMissingProfileAsync(ct);
        return View(new ProfileBackfillViewModel(missing));
    }

    [HttpPost("Run")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(CancellationToken ct = default)
    {
        var missing = await GetUsersMissingProfileAsync(ct);
        if (missing.Count == 0)
        {
            SetSuccess("All users already have a Profile — nothing to do.");
            return RedirectToAction(nameof(Index));
        }

        foreach (var row in missing)
        {
            // EnsureStubProfileAsync is idempotent — ProfileService takes a
            // per-userId lock around the GetByUserId/Add pair, so a parallel
            // signup creating the profile between count and run is handled
            // cleanly. The caching decorator refreshes the FullProfile entry
            // after the write so downstream reads see the new Stub immediately
            // (design-rules §2a/§2c).
            await _profileService.EnsureStubProfileAsync(row.UserId, ct);
        }

        _logger.LogInformation(
            "Stub Profile backfill: created {Count} profiles", missing.Count);
        SetSuccess($"Materialized {missing.Count} Stub Profiles.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<IReadOnlyList<MissingProfileRow>> GetUsersMissingProfileAsync(CancellationToken ct)
    {
        var users = await _userService.GetAllUsersAsync(ct);
        var userIds = users.Select(u => u.Id).ToList();
        var profiles = await _profileService.GetByUserIdsAsync(userIds, ct);

        return users
            .Where(u => !profiles.ContainsKey(u.Id))
            .Select(u => new MissingProfileRow(
                u.Id,
                u.Email ?? string.Empty,
                u.DisplayName,
                u.CreatedAt,
                u.ContactSource))
            .OrderByDescending(r => r.CreatedAt)
            .ToList();
    }
}

/// <summary>
/// Issue #635 (§15i): one row per User that has no <see cref="Profile"/>.
/// </summary>
public sealed record MissingProfileRow(
    Guid UserId,
    string Email,
    string DisplayName,
    Instant CreatedAt,
    ContactSource? ContactSource);

/// <summary>
/// Issue #635 (§15i): view model for the Stub Profile backfill admin page.
/// </summary>
public sealed record ProfileBackfillViewModel(IReadOnlyList<MissingProfileRow> MissingRows);
