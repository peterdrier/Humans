using Humans.Auth.Contracts;
using Humans.Backdoor.Models;
using Humans.Backdoor.Services;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Backdoor.Controllers;

/// <summary>
/// The section's one page, at <c>/Backdoor</c>: allocate, rotate and revoke the personal
/// keys that open <c>/api/backdoor/*</c> (nobodies-collective/Humans#1128).
/// </summary>
/// <remarks>
/// A freshly issued plaintext rides one redirect in TempData and is shown once. It is never
/// stored, so a lost key is rotated, not recovered.
/// </remarks>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Backdoor")]
internal sealed class BackdoorController(
    IBackdoorApiKeyService keys,
    IRoleAssignmentService roles,
    IUserServiceRead users) : HumansControllerBase(users)
{
    /// <summary>TempData slot carrying a just-issued plaintext across the post-redirect-get.</summary>
    private const string NewKeyTempDataKey = "BackdoorNewKey";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        return View(await BuildViewModelAsync(TempData[NewKeyTempDataKey] as string, ct));
    }

    [HttpPost("Issue")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Issue(Guid ownerUserId, string label, CancellationToken ct)
    {
        var (userMissing, actor) = await RequireCurrentUserAsync(ct);
        if (userMissing is not null) return userMissing;

        var result = await keys.IssueAsync(ownerUserId, label ?? string.Empty, actor.Id, ct);
        return RedirectAfter(result);
    }

    [HttpPost("{id:guid}/Rotate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rotate(Guid id, CancellationToken ct)
    {
        var (userMissing, actor) = await RequireCurrentUserAsync(ct);
        if (userMissing is not null) return userMissing;

        var result = await keys.RotateAsync(id, actor.Id, ct);
        return RedirectAfter(result);
    }

    [HttpPost("{id:guid}/Revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var (userMissing, actor) = await RequireCurrentUserAsync(ct);
        if (userMissing is not null) return userMissing;

        if (await keys.RevokeAsync(id, actor.Id, ct))
            SetSuccess("Key revoked.");
        else
            SetError("That key no longer exists or is already revoked.");

        return RedirectToAction(nameof(Index));
    }

    private IActionResult RedirectAfter(BackdoorKeyIssueResult result)
    {
        if (result.Succeeded)
        {
            TempData[NewKeyTempDataKey] = result.PlaintextKey;
            SetSuccess("Key created. Copy it now — it is not shown again.");
        }
        else
        {
            SetError(result.Error!);
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<BackdoorKeysViewModel> BuildViewModelAsync(string? newPlaintextKey, CancellationToken ct)
    {
        var rows = await keys.ListAsync(ct);

        // Admins ∪ Board, narrowed below to active accounts — the same test
        // IBackdoorApiKeyService enforces on issue, so the dropdown can never offer someone
        // the service would refuse.
        var eligibleIds = new HashSet<Guid>(await roles.GetActiveUserIdsInRoleAsync(RoleNames.Admin, ct));
        eligibleIds.UnionWith(await roles.GetActiveUserIdsInRoleAsync(RoleNames.Board, ct));

        var infos = await UserService.GetUserInfosAsync([.. eligibleIds.Union(rows.Select(r => r.UserId))], ct);

        // A role assignment outlives a suspension, so the role sets alone name accounts the
        // service refuses. Narrowing them by state gives the same two-part test
        // IBackdoorApiKeyService applies at issue and on every authentication — used both to
        // populate the dropdown and to tell a working key from an unrevoked but refused one.
        var eligibleOwnerIds = eligibleIds
            .Where(id => infos.GetValueOrDefault(id)?.State == UserState.Active)
            .ToHashSet();

        return new BackdoorKeysViewModel
        {
            // Newest first — a display sort, so it lives here rather than in the repository.
            Keys = [.. rows.OrderByDescending(r => r.CreatedAt).Select(r => new BackdoorKeyListItem(
                r.Id,
                r.UserId,
                infos.GetValueOrDefault(r.UserId)?.BurnerName ?? "(unknown)",
                r.Label,
                r.DisplayPrefix,
                r.CreatedAt,
                r.LastUsedAt,
                r.RevokedAt,
                eligibleOwnerIds.Contains(r.UserId)))],
            EligibleUsers = [.. eligibleOwnerIds
                .Select(id => new BackdoorKeyCandidate(id, infos[id].BurnerName))
                .OrderBy(c => c.DisplayName, StringComparer.CurrentCulture)],
            NewPlaintextKey = newPlaintextKey,
        };
    }
}
