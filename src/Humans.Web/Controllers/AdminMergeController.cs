using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
[Route("Admin/MergeRequests")]
public class AdminMergeController : HumansControllerBase
{
    private readonly IAccountMergeService _mergeService;
    private readonly IProfileService _profileService;
    private readonly ITeamService _teamService;
    private readonly ILogger<AdminMergeController> _logger;

    public AdminMergeController(
        UserManager<User> userManager,
        IAccountMergeService mergeService,
        IProfileService profileService,
        ITeamService teamService,
        ILogger<AdminMergeController> logger)
        : base(userManager)
    {
        _mergeService = mergeService;
        _profileService = profileService;
        _teamService = teamService;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var requests = await _mergeService.GetPendingRequestsAsync();

        var viewModel = new AccountMergeListViewModel
        {
            Requests = requests.Select(r => new AccountMergeRequestViewModel
            {
                Id = r.Id,
                Email = r.Email,
                PrimaryUserDisplayName = r.TargetUser.DisplayName,
                PrimaryUserEmail = r.TargetUser.Email,
                PrimaryUserId = r.TargetUserId,
                DuplicateUserDisplayName = r.SourceUser.DisplayName,
                DuplicateUserEmail = r.SourceUser.Email,
                DuplicateUserId = r.SourceUserId,
                CreatedAt = r.CreatedAt.ToDateTimeUtc()
            }).ToList()
        };

        return View(viewModel);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id)
    {
        var request = await _mergeService.GetByIdAsync(id);
        if (request is null)
            return NotFound();

        var primaryCard = await BuildProfileCardAsync(request.TargetUser);
        var duplicateCard = await BuildProfileCardAsync(request.SourceUser);

        var viewModel = new AccountMergeDetailViewModel
        {
            Id = request.Id,
            Email = request.Email,
            PrimaryUser = primaryCard,
            DuplicateUser = duplicateCard,
            Status = request.Status.ToString(),
            CreatedAt = request.CreatedAt.ToDateTimeUtc(),
            ResolvedAt = request.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = request.ResolvedByUser?.DisplayName,
            AdminNotes = request.AdminNotes
        };

        return View(viewModel);
    }

    private async Task<ProfileSummaryViewModel> BuildProfileCardAsync(User user)
    {
        var profile = await _profileService.GetProfileAsync(user.Id);
        var teams = await _teamService.GetUserTeamsAsync(user.Id);
        var activeTeamNames = teams
            .Where(m => m.LeftAt is null)
            .Select(m => m.Team?.Name ?? "Unknown")
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProfileSummaryViewModel
        {
            UserId = user.Id,
            DisplayName = user.DisplayName,
            Email = user.Email,
            ProfilePictureUrl = user.ProfilePictureUrl,
            PreferredLanguage = user.PreferredLanguage,
            MembershipTier = profile?.MembershipTier.ToString(),
            MembershipStatus = profile?.IsSuspended == true ? "Suspended"
                : profile?.IsApproved == true ? "Active" : "Pending",
            MemberSince = profile?.CreatedAt.ToDateTimeUtc(),
            LastLogin = user.LastLoginAt?.ToDateTimeUtc(),
            City = profile?.City,
            CountryCode = profile?.CountryCode,
            Teams = activeTeamNames
        };
    }

    [HttpPost("{id:guid}/Accept")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Accept(Guid id, string? notes)
    {
        var (error, user) = await ResolveCurrentUserAsync();
        if (error is not null) return error;

        try
        {
            await _mergeService.AcceptAsync(id, user.Id, notes);
            SetSuccess("Account merge completed. Duplicate account has been archived.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Failed to accept merge request {RequestId}", id);
            SetError($"Failed to accept merge: {ex.Message}");
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/Reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(Guid id, string? notes)
    {
        var (error, user) = await ResolveCurrentUserAsync();
        if (error is not null) return error;

        try
        {
            await _mergeService.RejectAsync(id, user.Id, notes);
            SetSuccess("Merge request rejected. No changes were made.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Failed to reject merge request {RequestId}", id);
            SetError($"Failed to reject merge: {ex.Message}");
        }

        return RedirectToAction(nameof(Index));
    }
}
