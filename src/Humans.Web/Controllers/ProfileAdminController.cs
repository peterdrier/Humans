using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.DTOs.EmailProblems;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
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

    public ProfileAdminController(
        UserManager<User> userManager,
        IEmailProblemsService emailProblems,
        IAccountMergeService accountMerge,
        IUserEmailService userEmails,
        IUserService users,
        IAuditLogService audit,
        ILogger<ProfileAdminController> logger)
        : base(userManager)
    {
        _emailProblems = emailProblems;
        _accountMerge = accountMerge;
        _userEmails = userEmails;
        _users = users;
        _audit = audit;
        _logger = logger;
    }

    [HttpGet("EmailProblems")]
    public async Task<IActionResult> EmailProblems(CancellationToken ct)
    {
        var report = await _emailProblems.ScanAsync(ct);

        var crossUser = new List<CrossUserConflictRow>();
        var singleUserMap = new Dictionary<Guid, List<string>>();
        var systemLevel = new List<SystemLevelIssueRow>();

        var allInvolvedUserIds = report.Problems
            .SelectMany(p => new[] { p.UserId, p.OtherUserId })
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var users = await _users.GetByIdsAsync(allInvolvedUserIds, ct);
        string DisplayName(Guid? id) =>
            id is Guid g && users.TryGetValue(g, out var u) ? u.DisplayName : "(unknown)";

        foreach (var p in report.Problems)
        {
            switch (p.Kind)
            {
                case EmailProblemKind.SharedAcrossUsers when p.UserId is Guid u1 && p.OtherUserId is Guid u2:
                    crossUser.Add(new CrossUserConflictRow(p.Email ?? "(unknown)", u1, DisplayName(u1), u2, DisplayName(u2)));
                    break;

                case EmailProblemKind.MultipleIsPrimary or EmailProblemKind.MultipleIsGoogle
                    or EmailProblemKind.ZeroIsPrimary or EmailProblemKind.ZeroIsGoogle
                    or EmailProblemKind.Unverified
                    when p.UserId is Guid u:
                    if (!singleUserMap.TryGetValue(u, out var list))
                    {
                        list = new List<string>();
                        singleUserMap[u] = list;
                    }
                    list.Add(p.Kind switch
                    {
                        EmailProblemKind.MultipleIsPrimary => "multiple IsPrimary",
                        EmailProblemKind.MultipleIsGoogle => "multiple IsGoogle",
                        EmailProblemKind.ZeroIsPrimary => "zero IsPrimary",
                        EmailProblemKind.ZeroIsGoogle => "zero IsGoogle",
                        EmailProblemKind.Unverified => $"unverified: {p.Email}",
                        _ => p.Kind.ToString()
                    });
                    break;

                case EmailProblemKind.OrphanUserEmail:
                    systemLevel.Add(new SystemLevelIssueRow(
                        p.Kind, p.UserEmailId, p.UserId,
                        $"Orphan UserEmail \"{p.Email}\" (was userId {p.UserId})"));
                    break;

                case EmailProblemKind.GhostExternalLogins:
                    systemLevel.Add(new SystemLevelIssueRow(
                        p.Kind, null, p.UserId,
                        $"Ghost AspNetUserLogins for userId {p.UserId}"));
                    break;
            }
        }

        var singleUser = singleUserMap
            .Select(kvp => new SingleUserIssueRow(kvp.Key, DisplayName(kvp.Key), kvp.Value))
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var vm = new EmailProblemsListViewModel
        {
            ScannedAt = report.ScannedAt,
            CrossUserConflicts = crossUser,
            SingleUserIssues = singleUser,
            SystemLevelIssues = systemLevel
        };

        return View(vm);
    }
}
