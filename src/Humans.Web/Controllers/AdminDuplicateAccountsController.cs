using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Humans.Application;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Admin/DuplicateAccounts")]
public class AdminDuplicateAccountsController(
    IUserService userService,
    IDuplicateAccountService duplicateService,
    ITeamServiceRead teamService,
    ILogger<AdminDuplicateAccountsController> logger) : HumansControllerBase(userService)
{
    private readonly IUserService _userService = userService;

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var groups = await duplicateService.DetectDuplicatesAsync();

        var viewModel = new DuplicateAccountListViewModel
        {
            Groups = groups.Select(g => new DuplicateAccountGroupViewModel
            {
                SharedEmail = g.SharedEmail,
                Accounts = g.Accounts.Select(a => new DuplicateAccountItemViewModel
                {
                    UserId = a.UserId,
                    DisplayName = a.DisplayName,
                    Email = a.Email,
                    ProfilePictureUrl = a.ProfilePictureUrl,
                    MembershipTier = a.MembershipTier,
                    MembershipStatus = a.MembershipStatus,
                    LastLogin = a.LastLogin,
                    CreatedAt = a.CreatedAt,
                    TeamCount = a.TeamCount,
                    RoleAssignmentCount = a.RoleAssignmentCount,
                    HasProfile = a.HasProfile,
                    IsProfileComplete = a.IsProfileComplete,
                    EmailSources = a.EmailSources
                }).ToList()
            }).ToList()
        };

        return View(viewModel);
    }

    [HttpGet("Detail")]
    public async Task<IActionResult> Detail(Guid userId1, Guid userId2)
    {
        var group = await duplicateService.GetDuplicateGroupAsync(userId1, userId2);
        if (group is null)
        {
            SetError("No email conflict found between these accounts.");
            return RedirectToAction(nameof(Index));
        }

        var account1 = group.Accounts.First(a => a.UserId == userId1);
        var account2 = group.Accounts.First(a => a.UserId == userId2);

        var info1 = await _userService.GetUserInfoAsync(userId1);
        var info2 = await _userService.GetUserInfoAsync(userId2);

        var profile1 = await BuildProfileCardAsync(userId1, account1, info1);
        var profile2 = await BuildProfileCardAsync(userId2, account2, info2);

        var viewModel = new DuplicateAccountDetailViewModel
        {
            SharedEmail = group.SharedEmail,
            Account1 = profile1,
            Account2 = profile2,
            Account1IdentityEmail = info1?.IdentityEmailColumn,
            Account2IdentityEmail = info2?.IdentityEmailColumn,
            Account1Emails = MapEmails(info1),
            Account2Emails = MapEmails(info2)
        };

        return View(viewModel);
    }

    [HttpPost("Resolve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(Guid sourceUserId, Guid targetUserId, string? notes)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error is not null) return error;

        try
        {
            await duplicateService.ResolveAsync(sourceUserId, targetUserId, user.Id, notes);
            SetSuccess("Duplicate account resolved. The empty account has been archived and logins re-linked.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Failed to resolve duplicate: source {SourceId}, target {TargetId}", sourceUserId, targetUserId);
            SetError($"Failed to resolve duplicate: {ex.Message}");
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<ProfileSummaryViewModel> BuildProfileCardAsync(
        Guid userId, DuplicateAccountInfo accountInfo, UserInfo? info)
    {
        var profile = info?.Profile;
        var activeTeamNames = (await teamService.GetTeamsAsync()).Values
            .Where(t => t.Members.Any(m => m.UserId == userId))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProfileSummaryViewModel
        {
            UserId = userId,
            DisplayName = accountInfo.DisplayName,
            Email = accountInfo.Email,
            ProfilePictureUrl = accountInfo.ProfilePictureUrl,
            MembershipTier = accountInfo.MembershipTier,
            MembershipStatus = accountInfo.MembershipStatus,
            MemberSince = profile?.CreatedAt.ToDateTimeUtc(),
            LastLogin = accountInfo.LastLogin,
            City = profile?.City,
            CountryCode = profile?.CountryCode,
            Teams = activeTeamNames
        };
    }

    private static List<DuplicateAccountEmailRowViewModel> MapEmails(UserInfo? info) =>
        info is null
            ? []
            : info.UserEmails
                .Select(e => new DuplicateAccountEmailRowViewModel
                {
                    Email = e.Email,
                    IsPrimary = e.IsPrimary,
                    IsVerified = e.IsVerified,
                    IsGoogle = e.IsGoogle,
                    Provider = e.Provider
                })
                .ToList();
}
