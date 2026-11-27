using Humans.Auth.Contracts;
using Humans.Backdoor.Models;
using Humans.Backdoor.Services;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Backdoor.Controllers;

/// <summary>
/// A freshly created plaintext rides one redirect in TempData and is shown once. It is never stored, so a lost key is
/// rotated, not recovered.
/// Anyone a <see cref="BackdoorScope"/> admits makes, rotates and revokes their own keys here. Board and Admin read
/// every key (whose it is, never the key itself); only Admin creates, rotates or revokes someone else's. The page is
/// not linked for anyone but Admin and Board.
/// </summary>
[Route("Backdoor")]
internal sealed class BackdoorController(
    IBackdoorApiKeyService keys,
    ISystemRoleService roles,
    IUserService users,
    IAuthorizationService authorization,
    IStringLocalizer<BackdoorResource> localizer) : HumansControllerBase(users)
{
    private const string NewKeyTempDataKey = "BackdoorNewKey";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (userMissing, me) = await RequireCurrentUserAsync(ct);
        if (userMissing is not null) return userMissing;

        if (!await SeesEveryKeyAsync() && !await keys.IsEligibleAsync(me.Id, ct)) return Forbid();

        return View(await BuildViewModelAsync(me.Id, TempData[NewKeyTempDataKey] as string, ct));
    }

    /// <summary>No owner means the caller's own key; naming someone else is Admin-only.</summary>
    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Guid? ownerUserId, string label, CancellationToken ct)
    {
        var (userMissing, actor) = await RequireCurrentUserAsync(ct);
        if (userMissing is not null) return userMissing;

        var owner = ownerUserId ?? actor.Id;
        if (!await MayHandleKeysOfAsync(owner, actor.Id))
        {
            SetError(localizer["Backdoor_Error_NotAllowed"]);
            return RedirectToAction(nameof(Index));
        }

        var result = await keys.CreateAsync(owner, label ?? string.Empty, actor.Id, ct);
        return RedirectAfter(result);
    }

    [HttpPost("{id:guid}/Rotate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rotate(Guid id, CancellationToken ct)
    {
        var (userMissing, actor) = await RequireCurrentUserAsync(ct);
        if (userMissing is not null) return userMissing;

        if (await KeyAccessErrorAsync(id, actor.Id, ct) is { } denied)
            return RedirectAfter(BackdoorKeyCreateResult.Failed(denied));

        var result = await keys.RotateAsync(id, actor.Id, ct);
        return RedirectAfter(result);
    }

    [HttpPost("{id:guid}/Revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var (userMissing, actor) = await RequireCurrentUserAsync(ct);
        if (userMissing is not null) return userMissing;

        if ((await KeyAccessErrorAsync(id, actor.Id, ct) ?? await keys.RevokeAsync(id, actor.Id, ct)) is { } error)
            SetError(localizer[$"Backdoor_Error_{error}"]);
        else
            SetSuccess(localizer["Backdoor_Revoked"]);

        return RedirectToAction(nameof(Index));
    }

    private IActionResult RedirectAfter(BackdoorKeyCreateResult result)
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

    private async Task<bool> SeesEveryKeyAsync() =>
        (await authorization.AuthorizeAsync(User, PolicyNames.BoardOrAdmin)).Succeeded;

    private async Task<bool> IsAdminAsync() =>
        (await authorization.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded;

    /// <summary>Your own keys always; anyone's only as a full Admin. Someone else's key reads as missing.</summary>
    private async Task<bool> MayHandleKeysOfAsync(Guid ownerUserId, Guid actorUserId) =>
        ownerUserId == actorUserId || await IsAdminAsync();

    private async Task<BackdoorKeyError?> KeyAccessErrorAsync(Guid keyId, Guid actorUserId, CancellationToken ct)
    {
        if ((await keys.ListAsync(ct)).FirstOrDefault(k => k.Id == keyId) is not { } key)
            return BackdoorKeyError.NotFound;
        return await MayHandleKeysOfAsync(key.UserId, actorUserId) ? null : BackdoorKeyError.NotAllowed;
    }

    private async Task<BackdoorKeysViewModel> BuildViewModelAsync(
        Guid me, string? newPlaintextKey, CancellationToken ct)
    {
        var rows = await keys.ListAsync(ct);
        var seesEveryKey = await SeesEveryKeyAsync();
        if (!seesEveryKey)
            rows = [.. rows.Where(r => r.UserId == me)];
        var isAdmin = await IsAdminAsync();

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
            EligibleUsers = isAdmin ? await OwnerCandidatesAsync(ct) : [],
            NewPlaintextKey = newPlaintextKey,
            ShortKeyLifetimeDays = (int)BackdoorApiKeyService.ShortKeyLifetime.TotalDays,
            ShowsOwners = seesEveryKey,
        };
    }

    /// <summary>Admin creates keys for Admin and Board members; anyone else a scope admits makes their own key.</summary>
    private async Task<IReadOnlyList<BackdoorKeyCandidate>> OwnerCandidatesAsync(CancellationToken ct)
    {
        var eligibleIds = new HashSet<Guid>(await roles.GetActiveUserIdsInRoleAsync(RoleNames.Admin, ct));
        eligibleIds.UnionWith(await roles.GetActiveUserIdsInRoleAsync(RoleNames.Board, ct));

        var infos = await UserService.GetUserInfosAsync([.. eligibleIds], ct);

        // A role assignment outlives a suspension, so the role sets alone name accounts the service refuses. Narrowing
        // by state matches the account-state half of the test IBackdoorApiKeyService applies at creation.
        var eligibleOwnerIds = eligibleIds
            .Where(id => infos.GetValueOrDefault(id)?.State == UserState.Active)
            .ToHashSet();

        return [.. eligibleOwnerIds
            .Select(id => new BackdoorKeyCandidate(id, infos[id].BurnerName))
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCulture)];
    }
}
