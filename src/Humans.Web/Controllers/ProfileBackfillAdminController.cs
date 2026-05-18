using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using NodaTime;

namespace Humans.Web.Controllers;

// Stub Profile backfill admin tool — see #635 (§15i). Idempotent.
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Profile/Admin/Backfill")]
public sealed class ProfileBackfillAdminController : HumansControllerBase
{
    private readonly IUserService _userService;
    private readonly IProfileService _profileService;
    private readonly ILogger<ProfileBackfillAdminController> _logger;

    public ProfileBackfillAdminController(
        IUserService userService,
        IProfileService profileService,
        ILogger<ProfileBackfillAdminController> logger)
        : base(userService)
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
            // Idempotent — ProfileService takes a per-userId lock around the get/add pair.
            await _profileService.EnsureStubProfileAsync(row.UserId, ct);
        }

        _logger.LogInformation(
            "Stub Profile backfill: created {Count} profiles", missing.Count);
        SetSuccess($"Materialized {missing.Count} Stub Profiles.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<IReadOnlyList<MissingProfileRow>> GetUsersMissingProfileAsync(CancellationToken ct)
    {
        IReadOnlyList<MissingProfileRow> rows = (await _userService.GetAllUserInfosAsync(ct).ConfigureAwait(false))
            .Where(u => u.Profile is null)
            .Select(u => new MissingProfileRow(
                u.Id,
                u.Email ?? string.Empty,
                u.BurnerName,
                u.CreatedAt,
                u.ContactSource))
            .OrderByDescending(r => r.CreatedAt)
            .ToList();
        return rows;
    }
}

public sealed record MissingProfileRow(
    Guid UserId,
    string Email,
    string DisplayName,
    Instant CreatedAt,
    ContactSource? ContactSource);

public sealed record ProfileBackfillViewModel(IReadOnlyList<MissingProfileRow> MissingRows);
