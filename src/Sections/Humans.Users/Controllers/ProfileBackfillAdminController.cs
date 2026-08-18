using Humans.UI.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.UI.Authorization;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Users.Controllers;

// Stub Profile backfill admin tool — see #635 (§15i). Idempotent.
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Profile/Admin/Backfill")]
internal sealed class ProfileBackfillAdminController(
    IUserService userService,
    ILogger<ProfileBackfillAdminController> logger) : HumansControllerBase(userService)
{
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
            // Idempotent — UserService takes a per-userId lock around the get/add pair.
            await userService.EnsureStubProfileAsync(row.UserId, ct: ct);
        }

        logger.LogInformation(
            "Stub Profile backfill: created {Count} profiles", missing.Count);
        SetSuccess($"Materialized {missing.Count} Stub Profiles.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<IReadOnlyList<MissingProfileRow>> GetUsersMissingProfileAsync(CancellationToken ct)
    {
        IReadOnlyList<MissingProfileRow> rows = (await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false))
            .Where(u => u.Profile is null && !u.IsTombstone)
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

internal sealed record MissingProfileRow(
    Guid UserId,
    string Email,
    string DisplayName,
    Instant CreatedAt,
    ContactSource? ContactSource);

internal sealed record ProfileBackfillViewModel(IReadOnlyList<MissingProfileRow> MissingRows);
