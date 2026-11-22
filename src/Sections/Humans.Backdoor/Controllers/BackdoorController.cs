using Humans.Auth.Contracts;
using Humans.Backdoor.Models;
using Humans.Backdoor.Services;
using Humans.Base.Constants;
using Humans.Base.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Backdoor.Controllers;

/// <summary>
/// A freshly issued plaintext rides one redirect in TempData and is shown once. It is never stored, so a lost key is
/// rotated, not recovered.
/// Anyone a <see cref="BackdoorScope"/> admits makes, rotates and revokes their own keys here. Board and Admin read
/// every key (who holds one, never the key itself); only Admin issues, rotates or revokes someone else's. The page is
/// not linked for anyone but Admin and Board.
/// </summary>
[Route("Backdoor")]
internal sealed class BackdoorController(
    IBackdoorApiKeyService keys,
    IRoleAssignmentService roles,
    IUserService users,
    IStringLocalizer<BackdoorResource> localizer) : HumansControllerBase(users)
{
    private const string NewKeyTempDataKey = "BackdoorNewKey";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (userMissing, me) = await RequireCurrentUserAsync(ct);
        if (userMissing is not null) return userMissing;

        if (!SeesEveryKey && !await keys.IsEligibleAsync(me.Id, ct)) return Forbid();

        return View(await BuildViewModelAsync(me.Id, TempData[NewKeyTempDataKey] as string, ct));
    }

    /// <summary>No owner means the caller's own key; naming someone else is Admin-only, enforced by the service.</summary>
    [HttpPost("Issue")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Issue(Guid? ownerUserId, string label, CancellationToken ct)
    {
        var (userMissing, actor) = await RequireCurrentUserAsync(ct);
        if (userMissing is not null) return userMissing;

        var result = await keys.IssueAsync(ownerUserId ?? actor.Id, label ?? string.Empty, actor.Id, ct);
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
            SetSuccess(localizer["Backdoor_Revoked"]);
        else
            SetError(localizer["Backdoor_Error_NotFound"]);

        return RedirectToAction(nameof(Index));
    }

    private IActionResult RedirectAfter(BackdoorKeyIssueResult result)
    {
        if (result.Succeeded)
        {
            TempData[NewKeyTempDataKey] = result.PlaintextKey;
            SetSuccess(localizer["Backdoor_Created_Flash"]);
        }
        else
        {
            SetError(localizer[$"Backdoor_Error_{result.Error}"]);
        }

        return RedirectToAction(nameof(Index));
    }

    private bool SeesEveryKey => User.IsInRole(RoleNames.Admin) || User.IsInRole(RoleNames.Board);

    private async Task<BackdoorKeysViewModel> BuildViewModelAsync(
        Guid me, string? newPlaintextKey, CancellationToken ct)
    {
        var rows = await keys.ListAsync(ct);
        if (!SeesEveryKey)
            rows = [.. rows.Where(r => r.UserId == me)];
        var isAdmin = User.IsInRole(RoleNames.Admin);

        return new BackdoorKeysViewModel
        {
            Keys = [.. rows.OrderByDescending(r => r.CreatedAt).Select(r => new BackdoorKeyListItem(
                r.Id,
                r.UserId,
                r.Label,
                r.DisplayPrefix,
                r.CreatedAt,
                r.LastUsedAt,
                r.RevokedAt,
                r.ExpiresAt,
                r.IsOwnerEligible,
                r.IsExpired,
                isAdmin || r.UserId == me))],
            EligibleUsers = isAdmin ? await AllocationCandidatesAsync(ct) : [],
            NewPlaintextKey = newPlaintextKey,
            ShortKeyLifetimeDays = (int)BackdoorApiKeyService.ShortKeyLifetime.TotalDays,
            ShowsOwners = SeesEveryKey,
        };
    }

    /// <summary>Admin allocates to Admin and Board holders; anyone else a scope admits makes their own key.</summary>
    private async Task<IReadOnlyList<BackdoorKeyCandidate>> AllocationCandidatesAsync(CancellationToken ct)
    {
        var eligibleIds = new HashSet<Guid>(await roles.GetActiveUserIdsInRoleAsync(RoleNames.Admin, ct));
        eligibleIds.UnionWith(await roles.GetActiveUserIdsInRoleAsync(RoleNames.Board, ct));

        var infos = await UserService.GetUserInfosAsync([.. eligibleIds], ct);

        // A role assignment outlives a suspension, so the role sets alone name accounts the service refuses. Narrowing
        // by state matches the account-state half of the test IBackdoorApiKeyService applies at issue.
        var eligibleOwnerIds = eligibleIds
            .Where(id => infos.GetValueOrDefault(id)?.State == UserState.Active)
            .ToHashSet();

        return [.. eligibleOwnerIds
            .Select(id => new BackdoorKeyCandidate(id, infos[id].BurnerName))
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCulture)];
    }
}
