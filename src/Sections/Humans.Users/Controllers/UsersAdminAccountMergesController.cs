using Humans.Users.Models;
using Humans.AuditLog.Contracts;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Base.Authorization;
using Humans.Users.Contracts;
using Humans.Users.Services;

namespace Humans.Users.Controllers;

/// <summary>
/// The admin's merge tools. People merge their own accounts through the emailed confirmation link; this page
/// is for the pairs that never go through it (detected duplicates, a pair picked by hand) and for looking
/// back at what was merged.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Users/Admin/AccountMerges")]
internal sealed class UsersAdminAccountMergesController(
    IUserManagement userService,
    IAccountMergeService mergeService,
    IDuplicateAccountService duplicateService,
    IAuditViewerService auditViewer,
    ILogger<UsersAdminAccountMergesController> logger) : HumansControllerBase(userService)
{
    private const int RecentMergeCount = 50;

    private readonly IUserManagement _userService = userService;

    /// <summary><paramref name="a"/> and <paramref name="b"/> put a pair picked by hand at the top.</summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(Guid? a, Guid? b, CancellationToken ct)
    {
        var groups = await duplicateService.DetectDuplicatesAsync(ct);

        // Raw: the already-merged test reads a tombstone's own MergedToUserId, which the resolving read hides.
        var infos = (await _userService.GetAllRawUserInfosAsync(ct)).ToDictionary(u => u.Id);

        // Only when this pair merged into each other, not into an unrelated account.
        bool MergedIntoEachOther(Guid x, Guid y) =>
            (infos.TryGetValue(x, out var ix) && ix.MergedToUserId == y) ||
            (infos.TryGetValue(y, out var iy) && iy.MergedToUserId == x);

        var rows = new List<AccountMergeRowViewModel>();

        if (a is { } pickedA && b is { } pickedB && pickedA != pickedB
            && infos.TryGetValue(pickedA, out var infoA) && infos.TryGetValue(pickedB, out var infoB))
        {
            rows.Add(new AccountMergeRowViewModel
            {
                AccountA = Card(infoA),
                AccountB = Card(infoB),
                AlreadyMerged = MergedIntoEachOther(pickedA, pickedB)
            });
        }

        foreach (var g in groups)
        {
            if (g.Accounts.Count < 2) continue;
            var a0 = g.Accounts[0];
            var a1 = g.Accounts[1];

            rows.Add(new AccountMergeRowViewModel
            {
                SharedEmail = g.SharedEmail,
                AccountA = Card(a0),
                AccountB = Card(a1),
                AlreadyMerged = MergedIntoEachOther(a0.UserId, a1.UserId)
            });
        }

        var merges = await auditViewer.GetFilteredAsync(
            AuditSubjectTypes.User, entityId: null, userId: null,
            actions: [UsersAuditActions.AccountMergeAccepted.Name, UsersAuditActions.AccountMergeFailed.Name],
            limit: RecentMergeCount, ct: ct);

        return View(new AccountMergeQueueViewModel
        {
            Rows = rows,
            Recent = merges
                .Where(e => e.RelatedEntityId.HasValue)
                .OrderByDescending(e => e.OccurredAt)
                .Select(e => new RecentMergeRow(
                    e.OccurredAt,
                    KeptUserId: e.RelatedEntityId!.Value,
                    MergedAwayUserId: e.EntityId,
                    // The account holder is one of the two accounts.
                    By: e.ActorUserId == e.EntityId || e.ActorUserId == e.RelatedEntityId ? null : e.ActorUserId,
                    Failed: string.Equals(e.Action.Name, UsersAuditActions.AccountMergeFailed.Name, StringComparison.Ordinal),
                    e.Description))
                .ToList()
        });
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

    private static ProfileSummaryViewModel Card(UserInfo user) => new()
    {
        UserId = user.Id,
        Email = user.Email,
        PreferredLanguage = user.PreferredLanguage,
        MembershipStatus = user.IsSuspended ? "Suspended" : user.State == UserState.Active ? "Active" : "Pending",
        LastLogin = user.LastLoginAt?.ToDateTimeUtc(),
        MemberSince = user.CreatedAt.ToDateTimeUtc(),
        HasProfile = user.Profile is not null
    };

    private static ProfileSummaryViewModel Card(DuplicateAccountInfo account) => new()
    {
        UserId = account.UserId,
        Email = account.Email,
        MembershipTier = account.MembershipTier,
        MembershipStatus = account.MembershipStatus,
        LastLogin = account.LastLogin,
        MemberSince = account.CreatedAt,
        Teams = account.Teams,
        HasProfile = account.HasProfile
    };
}
