using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Extensions;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Human")]
public class HumanController : Controller
{
    private readonly IProfileService _profileService;
    private readonly UserManager<User> _userManager;
    private readonly IAuditLogService _auditLogService;
    private readonly IOnboardingService _onboardingService;
    private readonly IClock _clock;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public HumanController(
        IProfileService profileService,
        UserManager<User> userManager,
        IAuditLogService auditLogService,
        IOnboardingService onboardingService,
        IClock clock,
        IStringLocalizer<SharedResource> localizer)
    {
        _profileService = profileService;
        _userManager = userManager;
        _auditLogService = auditLogService;
        _onboardingService = onboardingService;
        _clock = clock;
        _localizer = localizer;
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> View(Guid id)
    {
        var profile = await _profileService.GetProfileAsync(id);

        if (profile == null || profile.IsSuspended)
        {
            return NotFound();
        }

        var viewer = await _userManager.GetUserAsync(User);
        if (viewer == null)
        {
            return NotFound();
        }

        var isOwnProfile = viewer.Id == id;

        // The ProfileCard ViewComponent handles all data fetching and permission checks.
        var viewModel = new ProfileViewModel
        {
            Id = profile.Id,
            UserId = id,
            DisplayName = profile.User.DisplayName,
            IsOwnProfile = isOwnProfile,
            IsApproved = profile.IsApproved,
        };

        return View("~/Views/Profile/Index.cshtml", viewModel);
    }

    [Authorize(Roles = "Board,Admin")]
    [HttpGet("Admin")]
    public async Task<IActionResult> Humans(string? search, string? filter, string sort = "name", string dir = "asc", int page = 1)
    {
        var pageSize = 20;
        var allRows = await _profileService.GetFilteredHumansAsync(search, filter);
        var totalCount = allRows.Count;

        // Materialize for flexible sorting (fine at ~500 users)
        var allMatching = allRows.Select(r => new AdminHumanViewModel
        {
            Id = r.UserId,
            Email = r.Email,
            DisplayName = r.DisplayName,
            ProfilePictureUrl = r.ProfilePictureUrl,
            CreatedAt = r.CreatedAt,
            LastLoginAt = r.LastLoginAt,
            HasProfile = r.HasProfile,
            IsApproved = r.IsApproved,
            MembershipStatus = r.MembershipStatus
        }).ToList();

        var ascending = !string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);
        IEnumerable<AdminHumanViewModel> sorted = sort?.ToLowerInvariant() switch
        {
            "joined" => ascending
                ? allMatching.OrderBy(m => m.CreatedAt)
                : allMatching.OrderByDescending(m => m.CreatedAt),
            "login" => ascending
                ? allMatching.OrderBy(m => m.LastLoginAt.HasValue ? 0 : 1).ThenBy(m => m.LastLoginAt)
                : allMatching.OrderBy(m => m.LastLoginAt.HasValue ? 0 : 1).ThenByDescending(m => m.LastLoginAt),
            "status" => ascending
                ? allMatching.OrderBy(m => m.MembershipStatus, StringComparer.OrdinalIgnoreCase)
                : allMatching.OrderByDescending(m => m.MembershipStatus, StringComparer.OrdinalIgnoreCase),
            _ => ascending
                ? allMatching.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                : allMatching.OrderByDescending(m => m.DisplayName, StringComparer.OrdinalIgnoreCase),
        };

        var members = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var viewModel = new AdminHumanListViewModel
        {
            Humans = members,
            SearchTerm = search,
            StatusFilter = filter,
            SortBy = sort?.ToLowerInvariant() ?? "name",
            SortDir = ascending ? "asc" : "desc",
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View(viewModel);
    }

    [Authorize(Roles = "Board,Admin")]
    [HttpGet("Admin/{id:guid}")]
    public async Task<IActionResult> HumanDetail(Guid id)
    {
        var data = await _profileService.GetAdminHumanDetailAsync(id);
        if (data == null)
        {
            return NotFound();
        }

        var now = _clock.GetCurrentInstant();

        var viewModel = new AdminHumanDetailViewModel
        {
            UserId = data.User.Id,
            Email = data.User.Email ?? string.Empty,
            DisplayName = data.User.DisplayName,
            ProfilePictureUrl = data.User.ProfilePictureUrl,
            CreatedAt = data.User.CreatedAt.ToDateTimeUtc(),
            LastLoginAt = data.User.LastLoginAt?.ToDateTimeUtc(),
            IsSuspended = data.Profile?.IsSuspended ?? false,
            IsApproved = data.Profile?.IsApproved ?? false,
            HasProfile = data.Profile != null,
            AdminNotes = data.Profile?.AdminNotes,
            MembershipTier = data.Profile?.MembershipTier ?? MembershipTier.Volunteer,
            ConsentCheckStatus = data.Profile?.ConsentCheckStatus,
            IsRejected = data.Profile?.RejectedAt != null,
            RejectionReason = data.Profile?.RejectionReason,
            RejectedAt = data.Profile?.RejectedAt?.ToDateTimeUtc(),
            RejectedByName = data.RejectedByName,
            ApplicationCount = data.Applications.Count,
            ConsentCount = data.ConsentCount,
            Applications = data.Applications
                .Take(5)
                .Select(a => new AdminHumanApplicationViewModel
                {
                    Id = a.Id,
                    Status = a.Status.ToString(),
                    SubmittedAt = a.SubmittedAt.ToDateTimeUtc()
                }).ToList(),
            RoleAssignments = data.RoleAssignments.Select(ra => new AdminRoleAssignmentViewModel
            {
                Id = ra.Id,
                UserId = ra.UserId,
                RoleName = ra.RoleName,
                ValidFrom = ra.ValidFrom.ToDateTimeUtc(),
                ValidTo = ra.ValidTo?.ToDateTimeUtc(),
                Notes = ra.Notes,
                IsActive = ra.IsActive(now),
                CreatedByName = ra.CreatedByUser?.DisplayName,
                CreatedAt = ra.CreatedAt.ToDateTimeUtc()
            }).ToList(),
            AuditLog = data.AuditEntries.Select(e => new AuditLogEntryViewModel
            {
                Action = e.Action,
                Description = e.Description,
                OccurredAt = e.OccurredAt,
                ActorName = e.ActorName ?? string.Empty,
                IsSystemAction = e.IsSystemAction
            }).ToList()
        };

        return View(viewModel);
    }

    [Authorize(Roles = "Board,Admin")]
    [HttpPost("Admin/{id:guid}/Suspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SuspendHuman(Guid id, string? notes)
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null)
            return NotFound();

        var result = await _onboardingService.SuspendAsync(id, currentUser.Id, currentUser.DisplayName, notes);
        if (!result.Success)
            return NotFound();

        TempData["SuccessMessage"] = _localizer["Admin_MemberSuspended"].Value;
        return RedirectToAction(nameof(HumanDetail), new { id });
    }

    [Authorize(Roles = "Board,Admin")]
    [HttpPost("Admin/{id:guid}/Unsuspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnsuspendHuman(Guid id)
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null)
            return NotFound();

        var result = await _onboardingService.UnsuspendAsync(id, currentUser.Id, currentUser.DisplayName);
        if (!result.Success)
            return NotFound();

        TempData["SuccessMessage"] = _localizer["Admin_MemberUnsuspended"].Value;
        return RedirectToAction(nameof(HumanDetail), new { id });
    }

    [Authorize(Roles = "Board,Admin")]
    [HttpPost("Admin/{id:guid}/Approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveVolunteer(Guid id)
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null)
            return NotFound();

        var result = await _onboardingService.ApproveVolunteerAsync(id, currentUser.Id, currentUser.DisplayName);
        if (!result.Success)
            return NotFound();

        TempData["SuccessMessage"] = _localizer["Admin_VolunteerApproved"].Value;
        return RedirectToAction(nameof(HumanDetail), new { id });
    }

    [Authorize(Roles = "Board,Admin")]
    [HttpPost("Admin/{id:guid}/Reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectSignup(Guid id, string? reason)
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null)
            return Unauthorized();

        var result = await _onboardingService.RejectSignupAsync(id, currentUser.Id, currentUser.DisplayName, reason);
        if (!result.Success)
        {
            if (string.Equals(result.ErrorKey, "AlreadyRejected", StringComparison.Ordinal))
                TempData["ErrorMessage"] = "This human has already been rejected.";
            else
                return NotFound();
            return RedirectToAction(nameof(HumanDetail), new { id });
        }

        TempData["SuccessMessage"] = "Signup rejected.";
        return RedirectToAction(nameof(HumanDetail), new { id });
    }

    [Authorize(Roles = "Board,Admin")]
    [HttpGet("Admin/{id:guid}/GoogleSyncAudit")]
    public async Task<IActionResult> HumanGoogleSyncAudit(Guid id)
    {
        var user = await _userManager.FindByIdAsync(id.ToString());

        if (user == null)
        {
            return NotFound();
        }

        var entries = await _auditLogService.GetGoogleSyncByUserAsync(id);

        var viewModel = new GoogleSyncAuditListViewModel
        {
            Title = $"Google Sync Audit: {user.DisplayName}",
            BackUrl = Url.Action(nameof(HumanDetail), new { id }),
            BackLabel = "Back to Member Detail",
            Entries = entries.Select(e => new GoogleSyncAuditEntryViewModel
            {
                Action = e.Action.ToString(),
                Description = e.Description,
                UserEmail = e.UserEmail,
                Role = e.Role,
                SyncSource = e.SyncSource?.ToString(),
                OccurredAt = e.OccurredAt.ToDateTimeUtc(),
                Success = e.Success,
                ErrorMessage = e.ErrorMessage,
                ActorName = e.ActorName,
                ResourceName = e.Resource?.Name,
                ResourceId = e.ResourceId
            }).ToList()
        };

        return View("GoogleSyncAudit", viewModel);
    }
}
