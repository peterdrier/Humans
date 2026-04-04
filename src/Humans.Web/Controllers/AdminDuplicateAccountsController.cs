using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
[Route("Admin/DuplicateAccounts")]
public class AdminDuplicateAccountsController : HumansControllerBase
{
    private readonly IDuplicateAccountService _duplicateService;
    private readonly IProfileService _profileService;
    private readonly ITeamService _teamService;
    private readonly ILogger<AdminDuplicateAccountsController> _logger;

    public AdminDuplicateAccountsController(
        UserManager<User> userManager,
        IDuplicateAccountService duplicateService,
        IProfileService profileService,
        ITeamService teamService,
        ILogger<AdminDuplicateAccountsController> logger)
        : base(userManager)
    {
        _duplicateService = duplicateService;
        _profileService = profileService;
        _teamService = teamService;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var groups = await _duplicateService.DetectDuplicatesAsync();

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
        var group = await _duplicateService.GetDuplicateGroupAsync(userId1, userId2);
        if (group is null)
        {
            SetError("No email conflict found between these accounts.");
            return RedirectToAction(nameof(Index));
        }

        var account1 = group.Accounts.First(a => a.UserId == userId1);
        var account2 = group.Accounts.First(a => a.UserId == userId2);

        var profile1 = await BuildProfileCardAsync(userId1, account1);
        var profile2 = await BuildProfileCardAsync(userId2, account2);

        var viewModel = new DuplicateAccountDetailViewModel
        {
            SharedEmail = group.SharedEmail,
            Account1 = profile1,
            Account2 = profile2,
            Account1EmailSources = account1.EmailSources,
            Account2EmailSources = account2.EmailSources
        };

        return View(viewModel);
    }

    [HttpPost("Resolve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(Guid sourceUserId, Guid targetUserId, string? notes)
    {
        var (error, user) = await ResolveCurrentUserAsync();
        if (error is not null) return error;

        try
        {
            await _duplicateService.ResolveAsync(sourceUserId, targetUserId, user.Id, notes);
            SetSuccess("Duplicate account resolved. The empty account has been archived and logins re-linked.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Failed to resolve duplicate: source {SourceId}, target {TargetId}", sourceUserId, targetUserId);
            SetError($"Failed to resolve duplicate: {ex.Message}");
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<ProfileSummaryViewModel> BuildProfileCardAsync(
        Guid userId, DuplicateAccountInfo accountInfo)
    {
        var profile = await _profileService.GetProfileAsync(userId);
        var teams = await _teamService.GetUserTeamsAsync(userId);
        var activeTeamNames = teams
            .Where(m => m.LeftAt is null)
            .Select(m => m.Team?.Name ?? "Unknown")
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
}
