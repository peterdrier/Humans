using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces.Repositories;
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
    private readonly IProfileRepository _profileRepository;
    private readonly IClock _clock;
    private readonly ILogger<ProfileBackfillAdminController> _logger;

    public ProfileBackfillAdminController(
        UserManager<User> userManager,
        IUserService userService,
        IProfileRepository profileRepository,
        IClock clock,
        ILogger<ProfileBackfillAdminController> logger)
        : base(userManager)
    {
        _userService = userService;
        _profileRepository = profileRepository;
        _clock = clock;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var missing = await GetUsersMissingProfileAsync(ct);
        return View("ProfileBackfill", new ProfileBackfillViewModel(missing));
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

        var now = _clock.GetCurrentInstant();
        foreach (var row in missing)
        {
            // Re-check inside the loop in case a parallel signup created the
            // profile between count and run — keeps the operation idempotent
            // under concurrent writers.
            var existing = await _profileRepository.GetByUserIdAsync(row.UserId, ct);
            if (existing is not null) continue;

            var profile = new Profile
            {
                Id = Guid.NewGuid(),
                UserId = row.UserId,
                CreatedAt = now,
                UpdatedAt = now,
                State = ProfileState.Stub,
            };
            await _profileRepository.AddAsync(profile, ct);
        }

        _logger.LogInformation(
            "Issue #635 §15i Stub Profile backfill: created {Count} profiles", missing.Count);
        SetSuccess($"Materialized {missing.Count} Stub Profiles.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<IReadOnlyList<MissingProfileRow>> GetUsersMissingProfileAsync(CancellationToken ct)
    {
        var users = await _userService.GetAllUsersAsync(ct);
        var profiles = await _profileRepository.GetAllAsync(ct);
        var hasProfile = profiles.Select(p => p.UserId).ToHashSet();

        return users
            .Where(u => !hasProfile.Contains(u.Id))
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
