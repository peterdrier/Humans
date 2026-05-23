using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Admin/MergeRequests")]
public class AdminMergeController(
    IUserService userService,
    IAccountMergeService mergeService,
    ITeamServiceRead teamService,
    ILogger<AdminMergeController> logger) : HumansControllerBase(userService)
{
    private readonly IUserService _userService = userService;

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var requests = await mergeService.GetPendingRequestsAsync();

        var viewModel = new AccountMergeListViewModel
        {
            Requests = requests.Select(r => new AccountMergeRequestViewModel
            {
                Id = r.Id,
                Email = r.Email,
                PrimaryUserEmail = r.TargetUser.Email,
                PrimaryUserId = r.TargetUser.Id,
                DuplicateUserEmail = r.SourceUser.Email,
                DuplicateUserId = r.SourceUser.Id,
                CreatedAt = r.CreatedAt.ToDateTimeUtc()
            }).ToList()
        };

        return View(viewModel);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id)
    {
        var request = await mergeService.GetByIdAsync(id);
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
            ResolvedByName = request.ResolvedByDisplayName,
            AdminNotes = request.AdminNotes
        };

        return View(viewModel);
    }

    private async Task<ProfileSummaryViewModel> BuildProfileCardAsync(AccountMergeUserSnapshot user)
    {
        var profile = (await _userService.GetUserInfoAsync(user.Id))?.Profile;
        var activeTeamNames = (await teamService.GetTeamsAsync()).Values
            .Where(t => t.Members.Any(m => m.UserId == user.Id))
            .Select(t => t.Name)
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
            MembershipStatus = profile?.State == ProfileState.Suspended ? "Suspended"
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
        var (error, user) = await RequireCurrentUserAsync();
        if (error is not null) return error;

        try
        {
            await mergeService.AcceptAsync(id, user.Id, notes);
            SetSuccess("Account merge completed. Duplicate account has been archived.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Failed to accept merge request {RequestId}", id);
            SetError($"Failed to accept merge: {ex.Message}");
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/Reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(Guid id, string? notes)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error is not null) return error;

        try
        {
            await mergeService.RejectAsync(id, user.Id, notes);
            SetSuccess("Merge request rejected. No changes were made.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Failed to reject merge request {RequestId}", id);
            SetError($"Failed to reject merge: {ex.Message}");
        }

        return RedirectToAction(nameof(Index));
    }
}
