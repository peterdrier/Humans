using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Application;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models.EmailProblems;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Profile/Admin")]
public class ProfileAdminController(
    IUserService userService,
    IEmailProblemsService emailProblems,
    IAccountMergeService accountMerge,
    IUserEmailService userEmails,
    IUserService users,
    IAuditLogService audit,
    ILogger<ProfileAdminController> logger,
    IProfileService profileService,
    ITeamServiceRead teamService,
    IRoleAssignmentService roleAssignmentService) : HumansControllerBase(userService)
{
    private readonly IProfileService _profileService = profileService;

    [HttpGet("EmailProblems")]
    public async Task<IActionResult> EmailProblems(CancellationToken ct)
    {
        var report = await emailProblems.ScanAsync(ct);

        var allInvolvedUserIds = report.Problems
            .SelectMany(p => new[] { p.UserId, p.OtherUserId })
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var users1 = await users.GetUserInfosAsync(allInvolvedUserIds, ct);

        return View(EmailProblemsListViewModel.From(report, users1));
    }

    [HttpGet("EmailProblems/Compare")]
    public async Task<IActionResult> EmailProblemsCompare(Guid userId1, Guid userId2, CancellationToken ct)
    {
        if (userId1 == userId2)
        {
            SetError("Cannot compare a user against themselves.");
            return RedirectToAction(nameof(EmailProblems));
        }

        var ids = new[] { userId1, userId2 };
        var users1 = await users.GetByIdsAsync(ids, ct);
        if (!users1.TryGetValue(userId1, out var u1) || !users1.TryGetValue(userId2, out var u2))
        {
            SetError("One or both users not found.");
            return RedirectToAction(nameof(EmailProblems));
        }

        var info1 = await users.GetUserInfoAsync(userId1, ct);
        var info2 = await users.GetUserInfoAsync(userId2, ct);

        IReadOnlyList<UserEmailInfo> Emails(UserInfo? info) =>
            info?.UserEmails ?? [];

        var sharedEmail = Emails(info1)
            .Select(e => e.Email)
            .Intersect(Emails(info2).Select(e => e.Email), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? "(no exact match — see normalized)";

        CompareSide BuildSide(User user, UserInfo? info, int teamCount, int roleCount) =>
            new(user.Id, info?.BurnerName ?? string.Empty, user.ProfilePictureUrl,
                Emails(info),
                teamCount, roleCount,
                user.LastLoginAt,
                !string.IsNullOrEmpty(info?.Profile?.BurnerName));

        var allTeams = (await teamService.GetTeamsAsync(ct)).Values;
        var teamCount1 = allTeams.Count(t => t.Members.Any(m => m.UserId == userId1));
        var teamCount2 = allTeams.Count(t => t.Members.Any(m => m.UserId == userId2));
        var roles1 = await roleAssignmentService.GetByUserIdAsync(userId1, ct);
        var roles2 = await roleAssignmentService.GetByUserIdAsync(userId2, ct);

        var vm = new EmailProblemsCompareViewModel
        {
            SharedEmail = sharedEmail,
            Account1 = BuildSide(u1, info1,
                teamCount1,
                roles1.Count(r => r.ValidTo is null)),
            Account2 = BuildSide(u2, info2,
                teamCount2,
                roles2.Count(r => r.ValidTo is null))
        };

        return View(vm);
    }

    [HttpPost("EmailProblems/Merge")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Merge(
        Guid user1Id, Guid user2Id, Guid targetUserId, string? notes,
        CancellationToken ct)
    {
        var (authError, currentUser) = await RequireCurrentUserAsync();
        if (authError is not null) return authError;

        var (sourceUserId, validationError) =
            await ResolveAndValidateMergePairAsync(user1Id, user2Id, targetUserId, ct);
        if (validationError is not null) return validationError;

        try
        {
            await accountMerge.AdminMergeAsync(sourceUserId, targetUserId, currentUser.Id, notes, ct);
            SetSuccess("Accounts merged. The source account has been tombstoned.");
            return RedirectToAction(nameof(EmailProblems));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Admin-initiated merge failed: source {Source}, target {Target}: {Reason}",
                sourceUserId, targetUserId, ex.Message);
            SetError($"Merge failed: {ex.Message}");
            return RedirectToAction(nameof(EmailProblemsCompare),
                new { userId1 = user1Id, userId2 = user2Id });
        }
    }

    private async Task<(Guid SourceUserId, IActionResult? Error)> ResolveAndValidateMergePairAsync(
        Guid user1Id, Guid user2Id, Guid targetUserId, CancellationToken ct)
    {
        Guid sourceUserId;
        if (targetUserId == user1Id) sourceUserId = user2Id;
        else if (targetUserId == user2Id) sourceUserId = user1Id;
        else
        {
            SetError("Target must be one of the two compared accounts.");
            return (Guid.Empty, RedirectToAction(nameof(EmailProblemsCompare),
                new { userId1 = user1Id, userId2 = user2Id }));
        }

        if (!await emailProblems.UsersShareAnyEmailAsync(user1Id, user2Id, ct))
        {
            SetError("These accounts no longer share an email and cannot be merged here.");
            return (Guid.Empty, RedirectToAction(nameof(EmailProblems)));
        }

        return (sourceUserId, null);
    }

    [HttpPost("EmailProblems/DeleteOrphanEmail")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteOrphanEmail(Guid emailId, CancellationToken ct)
    {
        var (error, currentUser) = await RequireCurrentUserAsync();
        if (error is not null) return error;

        var deleted = await userEmails.DeleteByIdAsync(emailId, ct);
        if (deleted)
        {
            await audit.LogAsync(
                AuditAction.OrphanUserEmailDeleted, nameof(UserEmail), emailId,
                $"Orphan UserEmail row {emailId} deleted by EmailProblems action",
                currentUser.Id);
            SetSuccess("Orphan email row deleted.");
        }
        else
        {
            SetInfo("Already cleaned up — no row to delete.");
        }
        return RedirectToAction(nameof(EmailProblems));
    }

    [HttpPost("EmailProblems/BackfillLegacyEmails")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BackfillLegacyEmails(CancellationToken ct)
    {
        var (error, currentUser) = await RequireCurrentUserAsync();
        if (error is not null) return error;

        var backfilled = await emailProblems.BackfillLegacyIdentityEmailsAsync(currentUser.Id, ct);
        foreach (var (userId, email) in backfilled)
        {
            await audit.LogAsync(
                AuditAction.LegacyIdentityEmailBackfilled, nameof(User), userId,
                $"Backfilled verified UserEmail row from legacy User.Email column: {email}",
                currentUser.Id);
        }

        if (backfilled.Count == 0)
            SetInfo("No legacy User.Email values to backfill.");
        else
            SetSuccess($"Backfilled {backfilled.Count} verified UserEmail row(s) from legacy User.Email columns.");

        return RedirectToAction(nameof(EmailProblems));
    }
}
