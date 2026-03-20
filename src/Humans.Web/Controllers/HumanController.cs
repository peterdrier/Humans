using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Human")]
public class HumanController : HumansControllerBase
{
    private readonly IProfileService _profileService;
    private readonly IEmailService _emailService;
    private readonly UserManager<User> _userManager;
    private readonly IAuditLogService _auditLogService;
    private readonly IOnboardingService _onboardingService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IShiftSignupService _shiftSignupService;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IClock _clock;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly HumansDbContext _dbContext;

    public HumanController(
        IProfileService profileService,
        IEmailService emailService,
        UserManager<User> userManager,
        IAuditLogService auditLogService,
        IOnboardingService onboardingService,
        IRoleAssignmentService roleAssignmentService,
        IShiftSignupService shiftSignupService,
        IShiftManagementService shiftMgmt,
        IClock clock,
        IStringLocalizer<SharedResource> localizer,
        HumansDbContext dbContext)
        : base(userManager)
    {
        _profileService = profileService;
        _emailService = emailService;
        _userManager = userManager;
        _auditLogService = auditLogService;
        _onboardingService = onboardingService;
        _roleAssignmentService = roleAssignmentService;
        _shiftSignupService = shiftSignupService;
        _shiftMgmt = shiftMgmt;
        _clock = clock;
        _localizer = localizer;
        _dbContext = dbContext;
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> View(Guid id)
    {
        var profile = await _profileService.GetProfileAsync(id);

        if (profile == null || profile.IsSuspended)
        {
            return NotFound();
        }

        var viewer = await GetCurrentUserAsync();
        if (viewer == null)
        {
            return NotFound();
        }

        var isOwnProfile = viewer.Id == id;

        // Load no-show history for coordinators/NoInfoAdmin/Admin viewing other profiles
        List<NoShowHistoryItem>? noShowHistory = null;
        if (!isOwnProfile)
        {
            var viewerIsCoordinator = (await _shiftMgmt.GetCoordinatorDepartmentIdsAsync(viewer.Id)).Count > 0;
            var viewerCanViewShiftHistory = viewerIsCoordinator || ShiftRoleChecks.IsPrivilegedSignupApprover(User);

            if (viewerCanViewShiftHistory)
            {
                var noShows = await _shiftSignupService.GetNoShowHistoryAsync(id);
                if (noShows.Count > 0)
                {
                    noShowHistory = noShows.Select(s =>
                    {
                        var signupEs = s.Shift.Rota.EventSettings;
                        var signupTz = DateTimeZoneProviders.Tzdb[signupEs.TimeZoneId];
                        var shiftStart = s.Shift.GetAbsoluteStart(signupEs);
                        var zoned = shiftStart.InZone(signupTz);
                        return new NoShowHistoryItem
                        {
                            ShiftLabel = s.Shift.Rota.Name,
                            DepartmentName = s.Shift.Rota.Team?.Name ?? "",
                            ShiftDateLabel = zoned.ToDisplayShortDateTime(),
                            MarkedByName = s.ReviewedByUser?.DisplayName,
                            MarkedAtLabel = s.ReviewedAt?.InZone(signupTz).ToDisplayShortMonthDayTime()
                        };
                    }).ToList();
                }
            }
        }

        // The ProfileCard ViewComponent handles all data fetching and permission checks.
        var viewModel = new ProfileViewModel
        {
            Id = profile.Id,
            UserId = id,
            DisplayName = profile.User.DisplayName,
            IsOwnProfile = isOwnProfile,
            IsApproved = profile.IsApproved,
            NoShowHistory = noShowHistory,
        };

        return View("~/Views/Profile/Index.cshtml", viewModel);
    }

    [HttpGet("{id:guid}/SendMessage")]
    public async Task<IActionResult> SendMessage(Guid id)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser == null)
            return NotFound();

        if (currentUser.Id == id)
            return RedirectToAction(nameof(View), new { id });

        var targetUser = await FindUserByIdAsync(id);
        if (targetUser == null)
            return NotFound();

        var viewModel = new SendMessageViewModel
        {
            RecipientId = id,
            RecipientDisplayName = targetUser.DisplayName
        };

        return View(viewModel);
    }

    [HttpPost("{id:guid}/SendMessage")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendMessage(Guid id, SendMessageViewModel model)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser == null)
            return NotFound();

        if (currentUser.Id == id)
            return RedirectToAction(nameof(View), new { id });

        var targetUser = await _userManager.Users
            .Include(u => u.UserEmails)
            .FirstOrDefaultAsync(u => u.Id == id);
        if (targetUser == null)
            return NotFound();

        model.RecipientId = id;
        model.RecipientDisplayName = targetUser.DisplayName;

        if (!ModelState.IsValid)
            return View(model);

        // Strip any HTML tags from the message for safety
        var cleanMessage = System.Text.RegularExpressions.Regex.Replace(
            model.Message, "<[^>]+>", "", System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(1));

        var sender = await _userManager.Users
            .Include(u => u.UserEmails)
            .FirstAsync(u => u.Id == currentUser.Id);

        var recipientEmail = targetUser.GetEffectiveEmail() ?? targetUser.Email!;
        var senderEmail = sender.GetEffectiveEmail() ?? sender.Email!;

        await _emailService.SendFacilitatedMessageAsync(
            recipientEmail,
            targetUser.DisplayName,
            sender.DisplayName,
            cleanMessage,
            model.IncludeContactInfo,
            senderEmail,
            targetUser.PreferredLanguage);

        await _auditLogService.LogAsync(
            AuditAction.FacilitatedMessageSent,
            nameof(User), targetUser.Id,
            $"Message sent to {targetUser.DisplayName} (contact info shared: {(model.IncludeContactInfo ? "yes" : "no")})",
            currentUser.Id, currentUser.DisplayName);

        SetSuccess(string.Format(
            _localizer["SendMessage_Success"].Value,
            targetUser.DisplayName));

        return RedirectToAction(nameof(View), new { id });
    }

    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
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

    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    [HttpGet("{id:guid}/Admin")]
    public async Task<IActionResult> HumanDetail(Guid id)
    {
        var data = await _profileService.GetAdminHumanDetailAsync(id);
        if (data == null)
        {
            return NotFound();
        }

        var campaignGrants = await _dbContext.CampaignGrants
            .Include(g => g.Campaign)
            .Include(g => g.Code)
            .Where(g => g.UserId == id)
            .OrderByDescending(g => g.AssignedAt)
            .ToListAsync();
        ViewBag.CampaignGrants = campaignGrants;

        var outboxCount = await _dbContext.EmailOutboxMessages
            .CountAsync(m => m.UserId == id);
        ViewBag.OutboxCount = outboxCount;

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
            PreferredLanguage = data.User.PreferredLanguage,
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

    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    [HttpGet("{id:guid}/Outbox")]
    public async Task<IActionResult> Outbox(Guid id)
    {
        var messages = await _dbContext.EmailOutboxMessages
            .Where(m => m.UserId == id)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();

        ViewBag.HumanId = id;
        return View("~/Views/Profile/Outbox.cshtml", messages);
    }

    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Suspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SuspendHuman(Guid id, string? notes)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser == null)
            return NotFound();

        var result = await _onboardingService.SuspendAsync(id, currentUser.Id, currentUser.DisplayName, notes);
        if (!result.Success)
            return NotFound();

        SetSuccess(_localizer["Admin_MemberSuspended"].Value);
        return RedirectToAction(nameof(HumanDetail), new { id });
    }

    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Unsuspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnsuspendHuman(Guid id)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser == null)
            return NotFound();

        var result = await _onboardingService.UnsuspendAsync(id, currentUser.Id, currentUser.DisplayName);
        if (!result.Success)
            return NotFound();

        SetSuccess(_localizer["Admin_MemberUnsuspended"].Value);
        return RedirectToAction(nameof(HumanDetail), new { id });
    }

    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveVolunteer(Guid id)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser == null)
            return NotFound();

        var result = await _onboardingService.ApproveVolunteerAsync(id, currentUser.Id, currentUser.DisplayName);
        if (!result.Success)
            return NotFound();

        SetSuccess(_localizer["Admin_VolunteerApproved"].Value);
        return RedirectToAction(nameof(HumanDetail), new { id });
    }

    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectSignup(Guid id, string? reason)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser == null)
            return Unauthorized();

        var result = await _onboardingService.RejectSignupAsync(id, currentUser.Id, currentUser.DisplayName, reason);
        if (!result.Success)
        {
            if (string.Equals(result.ErrorKey, "AlreadyRejected", StringComparison.Ordinal))
                SetError("This human has already been rejected.");
            else
                return NotFound();
            return RedirectToAction(nameof(HumanDetail), new { id });
        }

        SetSuccess("Signup rejected.");
        return RedirectToAction(nameof(HumanDetail), new { id });
    }

    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    [HttpGet("{id:guid}/Admin/GoogleSyncAudit")]
    public async Task<IActionResult> HumanGoogleSyncAudit(Guid id)
    {
        var user = await FindUserByIdAsync(id);

        if (user == null)
        {
            return NotFound();
        }

        var entries = await _auditLogService.GetGoogleSyncByUserAsync(id);
        return GoogleSyncAuditView(
            $"Google Sync Audit: {user.DisplayName}",
            Url.Action(nameof(HumanDetail), new { id }),
            "Back to Member Detail",
            entries);
    }

    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    [HttpGet("{id:guid}/Admin/Roles/Add")]
    public async Task<IActionResult> AddRole(Guid id)
    {
        var user = await FindUserByIdAsync(id);
        if (user == null)
        {
            return NotFound();
        }

        var viewModel = new CreateRoleAssignmentViewModel
        {
            UserId = id,
            UserDisplayName = user.DisplayName,
            AvailableRoles = [.. RoleChecks.GetAssignableRoles(User)]
        };

        return View(viewModel);
    }

    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Roles/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddRole(Guid id, CreateRoleAssignmentViewModel model)
    {
        var user = await FindUserByIdAsync(id);
        if (user == null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(model.RoleName))
        {
            ModelState.AddModelError(nameof(model.RoleName), "Please select a role.");
            model.UserId = id;
            model.UserDisplayName = user.DisplayName;
            model.AvailableRoles = [.. RoleChecks.GetAssignableRoles(User)];
            return View(model);
        }

        // Enforce role assignment authorization
        if (!RoleChecks.CanManageRole(User, model.RoleName))
        {
            return Forbid();
        }

        var currentUser = await GetCurrentUserAsync();
        if (currentUser == null)
        {
            return Unauthorized();
        }

        var result = await _roleAssignmentService.AssignRoleAsync(
            id, model.RoleName, currentUser.Id, currentUser.DisplayName, model.Notes);

        if (!result.Success)
        {
            SetError(string.Format(_localizer["Admin_RoleAlreadyActive"].Value, model.RoleName));
            return RedirectToAction(nameof(HumanDetail), new { id });
        }

        SetSuccess(string.Format(_localizer["Admin_RoleAssigned"].Value, model.RoleName));
        return RedirectToAction(nameof(HumanDetail), new { id });
    }

    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Roles/{roleId:guid}/End")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EndRole(Guid id, Guid roleId, string? notes)
    {
        var roleAssignment = await _roleAssignmentService.GetByIdAsync(roleId);

        if (roleAssignment == null)
        {
            return NotFound();
        }

        // Enforce role assignment authorization
        if (!RoleChecks.CanManageRole(User, roleAssignment.RoleName))
        {
            return Forbid();
        }

        var currentUser = await GetCurrentUserAsync();
        if (currentUser == null)
        {
            return Unauthorized();
        }

        var result = await _roleAssignmentService.EndRoleAsync(
            roleId, currentUser.Id, currentUser.DisplayName, notes);

        if (!result.Success)
        {
            SetError(_localizer["Admin_RoleNotActive"].Value);
            return RedirectToAction(nameof(HumanDetail), new { id = roleAssignment.UserId });
        }

        SetSuccess(string.Format(_localizer["Admin_RoleEnded"].Value, roleAssignment.RoleName, roleAssignment.User.DisplayName));
        return RedirectToAction(nameof(HumanDetail), new { id = roleAssignment.UserId });
    }

}
