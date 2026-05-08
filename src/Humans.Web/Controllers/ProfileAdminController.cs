using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
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
public class ProfileAdminController : HumansControllerBase
{
    private readonly IEmailProblemsService _emailProblems;
    private readonly IAccountMergeService _accountMerge;
    private readonly IUserEmailService _userEmails;
    private readonly IUserService _users;
    private readonly IAuditLogService _audit;
    private readonly ILogger<ProfileAdminController> _logger;
    private readonly IProfileService _profileService;
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;

    public ProfileAdminController(
        UserManager<User> userManager,
        IEmailProblemsService emailProblems,
        IAccountMergeService accountMerge,
        IUserEmailService userEmails,
        IUserService users,
        IAuditLogService audit,
        ILogger<ProfileAdminController> logger,
        IProfileService profileService,
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService)
        : base(userManager)
    {
        _emailProblems = emailProblems;
        _accountMerge = accountMerge;
        _userEmails = userEmails;
        _users = users;
        _audit = audit;
        _logger = logger;
        _profileService = profileService;
        _teamService = teamService;
        _roleAssignmentService = roleAssignmentService;
    }

    [HttpGet("EmailProblems")]
    public async Task<IActionResult> EmailProblems(CancellationToken ct)
    {
        var report = await _emailProblems.ScanAsync(ct);

        var allInvolvedUserIds = report.Problems
            .SelectMany(p => new[] { p.UserId, p.OtherUserId })
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var users = await _users.GetByIdsAsync(allInvolvedUserIds, ct);

        return View(EmailProblemsListViewModel.From(report, users));
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
        var users = await _users.GetByIdsAsync(ids, ct);
        if (!users.TryGetValue(userId1, out var u1) || !users.TryGetValue(userId2, out var u2))
        {
            SetError("One or both users not found.");
            return RedirectToAction(nameof(EmailProblems));
        }

        var p1 = await _profileService.GetFullProfileAsync(userId1, ct);
        var p2 = await _profileService.GetFullProfileAsync(userId2, ct);

        var sharedEmail = (p1?.AllUserEmails ?? Array.Empty<UserEmailSnapshot>())
            .Select(e => e.Email)
            .Intersect((p2?.AllUserEmails ?? Array.Empty<UserEmailSnapshot>()).Select(e => e.Email),
                       StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? "(no exact match — see normalized)";

        CompareSide BuildSide(User user, FullProfile? profile, int teamCount, int roleCount) =>
            new(user.Id, user.DisplayName, user.ProfilePictureUrl,
                profile?.AllUserEmails ?? Array.Empty<UserEmailSnapshot>(),
                teamCount, roleCount,
                user.LastLoginAt,
                !string.IsNullOrEmpty(profile?.BurnerName));

        var memberships1 = await _teamService.GetUserTeamsAsync(userId1, ct);
        var memberships2 = await _teamService.GetUserTeamsAsync(userId2, ct);
        var roles1 = await _roleAssignmentService.GetByUserIdAsync(userId1, ct);
        var roles2 = await _roleAssignmentService.GetByUserIdAsync(userId2, ct);

        var vm = new EmailProblemsCompareViewModel
        {
            SharedEmail = sharedEmail,
            Account1 = BuildSide(u1, p1,
                memberships1.Count(m => m.LeftAt is null),
                roles1.Count(r => r.ValidTo is null)),
            Account2 = BuildSide(u2, p2,
                memberships2.Count(m => m.LeftAt is null),
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
            await _accountMerge.AdminMergeAsync(sourceUserId, targetUserId, currentUser.Id, notes, ct);
            SetSuccess("Accounts merged. The source account has been tombstoned.");
            return RedirectToAction(nameof(EmailProblems));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Admin-initiated merge failed: source {Source}, target {Target}: {Reason}",
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

        if (!await _emailProblems.UsersShareAnyEmailAsync(user1Id, user2Id, ct))
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

        var deleted = await _userEmails.DeleteByIdAsync(emailId, ct);
        if (deleted)
        {
            await _audit.LogAsync(
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

        var backfilled = await _emailProblems.BackfillLegacyIdentityEmailsAsync(ct);
        foreach (var (userId, email) in backfilled)
        {
            await _audit.LogAsync(
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

    [HttpPost("EmailProblems/DeleteGhostLogins")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteGhostLogins(Guid userId, CancellationToken ct)
    {
        var (error, currentUser) = await RequireCurrentUserAsync();
        if (error is not null) return error;

        if (!await _emailProblems.IsGhostExternalLoginsUserAsync(userId, ct))
        {
            SetInfo("Already cleaned up — user is no longer in the ghost set.");
            return RedirectToAction(nameof(EmailProblems));
        }

        var count = await _users.DeleteAllExternalLoginsForUserAsync(userId, ct);
        if (count > 0)
        {
            await _audit.LogAsync(
                AuditAction.GhostExternalLoginsDeleted, nameof(User), userId,
                $"Deleted {count} ghost AspNetUserLogins row(s) for userId {userId}",
                currentUser.Id);
            SetSuccess($"Deleted {count} ghost login row(s).");
        }
        else
        {
            SetInfo("Already cleaned up — no rows to delete.");
        }
        return RedirectToAction(nameof(EmailProblems));
    }
}
