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
using Humans.Web.Helpers;
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
    private readonly IGoogleWorkspaceUserService _workspaceUserService;
    private readonly IUserEmailService _userEmailService;
    private readonly IContactService _contactService;
    private readonly IClock _clock;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly HumansDbContext _dbContext;
    private readonly ILogger<HumanController> _logger;

    public HumanController(
        IProfileService profileService,
        IEmailService emailService,
        UserManager<User> userManager,
        IAuditLogService auditLogService,
        IOnboardingService onboardingService,
        IRoleAssignmentService roleAssignmentService,
        IShiftSignupService shiftSignupService,
        IShiftManagementService shiftMgmt,
        IGoogleWorkspaceUserService workspaceUserService,
        IUserEmailService userEmailService,
        IContactService contactService,
        IClock clock,
        IStringLocalizer<SharedResource> localizer,
        HumansDbContext dbContext,
        ILogger<HumanController> logger)
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
        _workspaceUserService = workspaceUserService;
        _userEmailService = userEmailService;
        _contactService = contactService;
        _clock = clock;
        _localizer = localizer;
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> View(Guid id)
    {
        var profile = await _profileService.GetProfileAsync(id);

        if (profile is null || profile.IsSuspended)
        {
            return NotFound();
        }

        var viewer = await GetCurrentUserAsync();
        if (viewer is null)
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

    [HttpGet("{id:guid}/Popover")]
    public async Task<IActionResult> Popover(Guid id)
    {
        var profile = await _profileService.GetProfileAsync(id);
        if (profile is null) return NotFound();

        var teams = await _dbContext.TeamMembers
            .Where(tm => tm.UserId == id && tm.LeftAt == null)
            .Select(tm => tm.Team!.Name)
            .OrderBy(n => n)
            .ToListAsync();

        var vm = new ProfileSummaryViewModel
        {
            UserId = id,
            DisplayName = profile.User.DisplayName,
            Email = profile.User.Email,
            ProfilePictureUrl = profile.User.ProfilePictureUrl,
            MembershipTier = profile.MembershipTier.ToString(),
            MembershipStatus = profile.IsSuspended ? "Suspended"
                : profile.IsApproved ? "Active" : "Pending",
            Teams = teams
        };

        return PartialView("_HumanPopover", vm);
    }

    [HttpGet("{id:guid}/SendMessage")]
    public async Task<IActionResult> SendMessage(Guid id)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
            return NotFound();

        if (currentUser.Id == id)
            return RedirectToAction(nameof(View), new { id });

        var targetUser = await FindUserByIdAsync(id);
        if (targetUser is null)
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
        if (currentUser is null)
            return NotFound();

        if (currentUser.Id == id)
            return RedirectToAction(nameof(View), new { id });

        var targetUser = await _userManager.Users
            .Include(u => u.UserEmails)
            .FirstOrDefaultAsync(u => u.Id == id);
        if (targetUser is null)
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

        var recipientEmail = targetUser.GetEffectiveEmail() ?? targetUser.Email;
        if (string.IsNullOrWhiteSpace(recipientEmail))
        {
            ModelState.AddModelError(string.Empty, _localizer["Common_Error"].Value);
            return View(model);
        }

        var senderEmail = sender.GetEffectiveEmail() ?? sender.Email;
        if (string.IsNullOrWhiteSpace(senderEmail))
        {
            ModelState.AddModelError(string.Empty, _localizer["Common_Error"].Value);
            return View(model);
        }

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

        // Load @nobodies.team email status for all users (fine at ~500 users)
        var nobodiesTeamEmails = await _dbContext.UserEmails
            .AsNoTracking()
            .Where(ue => ue.IsVerified && EF.Functions.ILike(ue.Email, "%@nobodies.team"))
            .Select(ue => new { ue.UserId, ue.IsNotificationTarget })
            .ToListAsync();

        var nobodiesTeamByUser = nobodiesTeamEmails
            .GroupBy(e => e.UserId)
            .ToDictionary(
                g => g.Key,
                g => g.Any(e => e.IsNotificationTarget));

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
            MembershipStatus = r.MembershipStatus,
            HasNobodiesTeamEmail = nobodiesTeamByUser.ContainsKey(r.UserId),
            NobodiesTeamEmailIsPrimary = nobodiesTeamByUser.TryGetValue(r.UserId, out var isPrimary) && isPrimary
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
        if (data is null)
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
            HasProfile = data.Profile is not null,
            AdminNotes = data.Profile?.AdminNotes,
            PreferredLanguage = data.User.PreferredLanguage,
            MembershipTier = data.Profile?.MembershipTier ?? MembershipTier.Volunteer,
            ConsentCheckStatus = data.Profile?.ConsentCheckStatus,
            IsRejected = data.Profile?.RejectedAt is not null,
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
                    Status = a.Status,
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
                Action = Enum.TryParse<AuditAction>(e.Action, out var parsedAction) ? parsedAction : default,
                Description = e.Description,
                OccurredAt = e.OccurredAt,
                ActorName = e.ActorName ?? string.Empty,
                IsSystemAction = e.IsSystemAction
            }).ToList()
        };

        // Check for @nobodies.team email
        viewModel.NobodiesTeamEmail = await _dbContext.UserEmails
            .Where(ue => ue.UserId == id && ue.IsVerified && EF.Functions.ILike(ue.Email, "%@nobodies.team"))
            .Select(ue => ue.Email)
            .FirstOrDefaultAsync();

        return View(viewModel);
    }

    [Authorize(Roles = RoleNames.Admin)]
    [HttpPost("{id:guid}/ProvisionEmail")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ProvisionEmail(Guid id, string emailPrefix)
    {
        if (string.IsNullOrWhiteSpace(emailPrefix))
        {
            SetError("Email prefix is required.");
            return RedirectToAction(nameof(HumanDetail), new { id });
        }

        var user = await _dbContext.Users
            .Include(u => u.UserEmails)
            .FirstOrDefaultAsync(u => u.Id == id);
        if (user is null)
            return NotFound();

        var fullEmail = $"{emailPrefix.Trim().ToLowerInvariant()}@nobodies.team";

        try
        {
            // Check if account already exists in Google Workspace
            var existing = await _workspaceUserService.GetAccountAsync(fullEmail);
            if (existing is not null)
            {
                SetError($"Account {fullEmail} already exists in Google Workspace.");
                return RedirectToAction(nameof(HumanDetail), new { id });
            }

            // Use their current notification target as recovery email (if not @nobodies.team)
            var recoveryEmail = user.GetEffectiveEmail();
            if (recoveryEmail?.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase) == true)
                recoveryEmail = user.Email; // fall back to OAuth email

            // Generate temp password and provision in Google Workspace
            var tempPassword = PasswordGenerator.GenerateTemporary();
            var nameParts = user.DisplayName.Split(' ', 2);
            await _workspaceUserService.ProvisionAccountAsync(
                fullEmail, nameParts[0], nameParts.Length > 1 ? nameParts[1] : "", tempPassword,
                recoveryEmail);

            // Auto-link: add as verified UserEmail (also sets notification target for @nobodies.team)
            await _userEmailService.AddVerifiedEmailAsync(id, fullEmail);

            // Auto-set as Google service email
            user.GoogleEmail = fullEmail;
            await _userManager.UpdateAsync(user);

            // Audit
            var currentUser = await GetCurrentUserAsync();
            if (currentUser is not null)
            {
                await _auditLogService.LogAsync(
                    AuditAction.WorkspaceAccountProvisioned,
                    "WorkspaceAccount", id,
                    $"Provisioned and linked @nobodies.team account: {fullEmail}",
                    currentUser.Id, currentUser.DisplayName);
                await _dbContext.SaveChangesAsync();
            }

            SetSuccess($"Account {fullEmail} provisioned and linked. Temporary password: {tempPassword}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision @nobodies.team account {Email} for user {UserId}", fullEmail, id);
            SetError($"Failed to provision {fullEmail}. Check logs for details.");
        }

        return RedirectToAction(nameof(HumanDetail), new { id });
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
        if (currentUser is null)
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
        if (currentUser is null)
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
        if (currentUser is null)
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
        if (currentUser is null)
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

        if (user is null)
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
        if (user is null)
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
        if (user is null)
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
        if (currentUser is null)
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

        if (roleAssignment is null)
        {
            return NotFound();
        }

        // Enforce role assignment authorization
        if (!RoleChecks.CanManageRole(User, roleAssignment.RoleName))
        {
            return Forbid();
        }

        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
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

    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    [HttpGet("Admin/Contacts")]
    public async Task<IActionResult> Contacts(string? search)
    {
        try
        {
            var allRows = await _contactService.GetFilteredContactsAsync(search);

            var viewModel = new AdminContactListViewModel
            {
                TotalCount = allRows.Count,
                SearchTerm = search,
                Contacts = allRows.Select(r => new AdminContactViewModel
                {
                    Id = r.UserId,
                    Email = r.Email,
                    DisplayName = r.DisplayName,
                    ContactSource = r.ContactSource,
                    ExternalSourceId = r.ExternalSourceId,
                    CreatedAt = r.CreatedAt,
                    HasCommunicationPreferences = r.HasCommunicationPreferences
                }).ToList()
            };

            return View(viewModel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading contacts list");
            SetError(_localizer["Common_Error"].Value);
            return RedirectToAction(nameof(Humans));
        }
    }

    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    [HttpGet("Admin/Contacts/{id:guid}")]
    public async Task<IActionResult> ContactDetail(Guid id)
    {
        try
        {
            var contact = await _contactService.GetContactDetailAsync(id);
            if (contact is null)
                return NotFound();

            var auditLog = await _dbContext.AuditLogEntries
                .AsNoTracking()
                .Where(a => a.EntityId == id)
                .OrderByDescending(a => a.OccurredAt)
                .ToListAsync();

            var viewModel = new AdminContactDetailViewModel
            {
                UserId = contact.Id,
                Email = contact.Email ?? string.Empty,
                DisplayName = contact.DisplayName,
                ContactSource = contact.ContactSource,
                ExternalSourceId = contact.ExternalSourceId,
                CreatedAt = contact.CreatedAt,
                CommunicationPreferences = contact.CommunicationPreferences.ToList(),
                AuditLog = auditLog
            };

            return View(viewModel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading contact detail for {ContactId}", id);
            SetError(_localizer["Common_Error"].Value);
            return RedirectToAction(nameof(Contacts));
        }
    }

    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    [HttpGet("Admin/Contacts/Create")]
    public IActionResult CreateContact()
    {
        return View(new CreateContactViewModel());
    }

    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    [HttpPost("Admin/Contacts/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateContact(CreateContactViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        try
        {
            var currentUser = await GetCurrentUserAsync();
            if (currentUser is null)
                return Unauthorized();

            var contact = await _contactService.CreateContactAsync(
                model.Email, model.DisplayName, model.Source);

            SetSuccess($"Contact created for {model.Email}.");
            return RedirectToAction(nameof(ContactDetail), new { id = contact.Id });
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating contact for {Email}", model.Email);
            ModelState.AddModelError(string.Empty, _localizer["Common_Error"].Value);
            return View(model);
        }
    }

}
