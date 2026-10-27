using Humans.Users.Models;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Base.Authorization;
using Humans.Users.Contracts;
using Humans.Users.Services;

namespace Humans.Users.Controllers;

[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Users/Admin/AccountMerges")]
internal sealed class UsersAdminAccountMergesController(
    IUserManagement userService,
    IAccountMergeService mergeService,
    IDuplicateAccountService duplicateService,
    ILogger<UsersAdminAccountMergesController> logger) : HumansControllerBase(userService)
{
    private readonly IUserManagement _userService = userService;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var requests = await mergeService.GetPendingRequestsAsync(ct);
        var groups = await duplicateService.DetectDuplicatesAsync(ct);

        var requestedPairs = requests
            .Select(r => PairKey(r.TargetUser.Id, r.SourceUser.Id))
            .ToHashSet(StringComparer.Ordinal);

        var involvedIds = requests
            .SelectMany(r => new[] { r.TargetUser.Id, r.SourceUser.Id })
            .Concat(groups.SelectMany(g => g.Accounts.Select(a => a.UserId)))
            .ToHashSet();

        // Raw: the already-merged test reads a tombstone's own MergedToUserId, which the resolving read hides.
        var infos = (await _userService.GetAllRawUserInfosAsync(ct))
            .Where(u => involvedIds.Contains(u.Id))
            .ToDictionary(u => u.Id);

        // Only when this pair merged into each other, not into an unrelated account.
        bool MergedIntoEachOther(Guid a, Guid b) =>
            (infos.TryGetValue(a, out var ia) && ia.MergedToUserId == b) ||
            (infos.TryGetValue(b, out var ib) && ib.MergedToUserId == a);

        var rows = new List<AccountMergeRowViewModel>();

        foreach (var r in requests.OrderBy(r => r.CreatedAt))
        {
            rows.Add(new AccountMergeRowViewModel
            {
                RequestId = r.Id,
                SharedEmail = r.Email,
                AccountA = Card(r.TargetUser),
                AccountB = Card(r.SourceUser),
                FromUserRequest = true,
                RequestedAt = r.CreatedAt.ToDateTimeUtc(),
                AlreadyMerged = MergedIntoEachOther(r.TargetUser.Id, r.SourceUser.Id)
            });
        }

        foreach (var g in groups)
        {
            if (g.Accounts.Count < 2) continue;
            var a0 = g.Accounts[0];
            var a1 = g.Accounts[1];
            if (requestedPairs.Contains(PairKey(a0.UserId, a1.UserId))) continue;

            rows.Add(new AccountMergeRowViewModel
            {
                RequestId = null,
                SharedEmail = g.SharedEmail,
                AccountA = Card(a0),
                AccountB = Card(a1),
                FromUserRequest = false,
                RequestedAt = null,
                AlreadyMerged = MergedIntoEachOther(a0.UserId, a1.UserId)
            });
        }

        return View(new AccountMergeQueueViewModel { Rows = rows });
    }

    [HttpPost("Merge")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Merge(Guid survivorUserId, Guid archivedUserId, string? notes, CancellationToken ct)
    {
        var (error, admin) = await RequireCurrentUserAsync(ct);
        if (error is not null) return error;

        var result = await mergeService.MergeAsync(survivorUserId, archivedUserId, admin.Id, notes, null, ct);
        if (result.Refusal is { } refusal)
        {
            logger.LogWarning("Merge refused for {Survivor} <- {Archived} by {AdminId}: {Reason}", survivorUserId, archivedUserId, admin.Id, refusal);
            SetError($"Merge failed: {refusal}");
        }
        else
            SetSuccess("Accounts merged. The archived account has been tombstoned.");

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{requestId:guid}/Merge")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MergeRequest(Guid requestId, Guid survivorUserId, string? notes, CancellationToken ct)
    {
        var (error, admin) = await RequireCurrentUserAsync(ct);
        if (error is not null) return error;

        var result = await mergeService.AcceptAsync(requestId, admin.Id, survivorUserId, notes, ct);
        if (result.Refusal is { } refusal)
        {
            logger.LogWarning("Merge refused for merge request {RequestId} by {AdminId}: {Reason}", requestId, admin.Id, refusal);
            SetError($"Merge failed: {refusal}");
        }
        else
            SetSuccess("Account merge completed. The archived account has been tombstoned.");

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{requestId:guid}/Dismiss")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Dismiss(Guid requestId, string? notes, CancellationToken ct)
    {
        var (error, admin) = await RequireCurrentUserAsync(ct);
        if (error is not null) return error;

        var result = await mergeService.RejectAsync(requestId, admin.Id, notes, ct);
        if (result.Refusal is { } refusal)
        {
            logger.LogWarning("Dismiss refused for merge request {RequestId} by {AdminId}: {Reason}", requestId, admin.Id, refusal);
            SetError($"Dismiss failed: {refusal}");
        }
        else
            SetSuccess("Merge request dismissed. No changes were made.");

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{requestId:guid}/Close")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(Guid requestId, CancellationToken ct)
    {
        var (error, admin) = await RequireCurrentUserAsync(ct);
        if (error is not null) return error;

        var result = await mergeService.ReconcileMergedRequestAsync(requestId, admin.Id, ct);
        if (result.Refusal is { } refusal)
        {
            logger.LogWarning("Close refused for merge request {RequestId} by {AdminId}: {Reason}", requestId, admin.Id, refusal);
            SetError($"Close failed: {refusal}");
        }
        else
            SetSuccess("Orphan merge request closed; the accounts were already merged.");

        return RedirectToAction(nameof(Index));
    }

    private static string PairKey(Guid a, Guid b)
    {
        var sa = a.ToString();
        var sb = b.ToString();
        return string.CompareOrdinal(sa, sb) <= 0 ? $"{sa}:{sb}" : $"{sb}:{sa}";
    }

    private static ProfileSummaryViewModel Card(AccountMergeUserSnapshot user) => new()
    {
        UserId = user.Id,
        DisplayName = user.DisplayName,
        Email = user.Email,
        ProfilePictureUrl = user.ProfilePictureUrl,
        PreferredLanguage = user.PreferredLanguage,
        LastLogin = user.LastLoginAt?.ToDateTimeUtc()
    };

    private static ProfileSummaryViewModel Card(DuplicateAccountInfo account) => new()
    {
        UserId = account.UserId,
        DisplayName = account.DisplayName,
        Email = account.Email,
        ProfilePictureUrl = account.ProfilePictureUrl,
        MembershipTier = account.MembershipTier,
        MembershipStatus = account.MembershipStatus,
        LastLogin = account.LastLogin,
        MemberSince = account.CreatedAt,
        Teams = account.Teams,
        HasProfile = account.HasProfile
    };
}
