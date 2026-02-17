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
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Humans.Web.Extensions;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize(Roles = "Board,Admin")]
[Route("Admin")]
public class AdminController : Controller
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly ITeamService _teamService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IAuditLogService _auditLogService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IClock _clock;
    private readonly ILogger<AdminController> _logger;
    private readonly SystemTeamSyncJob _systemTeamSyncJob;
    private readonly HumansMetricsService _metrics;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public AdminController(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        ITeamService teamService,
        IGoogleSyncService googleSyncService,
        IAuditLogService auditLogService,
        IMembershipCalculator membershipCalculator,
        IRoleAssignmentService roleAssignmentService,
        IClock clock,
        ILogger<AdminController> logger,
        SystemTeamSyncJob systemTeamSyncJob,
        HumansMetricsService metrics,
        IStringLocalizer<SharedResource> localizer)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _teamService = teamService;
        _googleSyncService = googleSyncService;
        _auditLogService = auditLogService;
        _membershipCalculator = membershipCalculator;
        _roleAssignmentService = roleAssignmentService;
        _clock = clock;
        _logger = logger;
        _systemTeamSyncJob = systemTeamSyncJob;
        _metrics = metrics;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var totalMembers = await _dbContext.Users.CountAsync();
        var pendingApplications = await _dbContext.Applications
            .CountAsync(a => a.Status == ApplicationStatus.Submitted || a.Status == ApplicationStatus.UnderReview);
        var pendingVolunteers = await _dbContext.Profiles
            .CountAsync(p => !p.IsApproved && !p.IsSuspended);

        // Calculate users with missing required consents (Volunteers docs = global)
        var allUserIds = await _dbContext.Users.Select(u => u.Id).ToListAsync();
        var usersWithAllVolunteerConsents = await _membershipCalculator.GetUsersWithAllRequiredConsentsAsync(allUserIds);

        // Also count Leads missing their team-specific docs
        var leadUserIds = await _dbContext.TeamMembers
            .Where(tm => tm.LeftAt == null && tm.Role == TeamMemberRole.Lead && tm.Team.SystemTeamType == SystemTeamType.None)
            .Select(tm => tm.UserId)
            .Distinct()
            .ToListAsync();
        var leadsWithAllConsents = leadUserIds.Count > 0
            ? await _membershipCalculator.GetUsersWithAllRequiredConsentsForTeamAsync(leadUserIds, SystemTeamIds.Leads)
            : new HashSet<Guid>();

        // A user has pending consents if missing any Volunteers doc OR (if they're a Lead) any Leads doc
        var pendingConsents = allUserIds.Count(id =>
            !usersWithAllVolunteerConsents.Contains(id) ||
            (leadUserIds.Contains(id) && !leadsWithAllConsents.Contains(id)));

        var recentActivity = await _dbContext.AuditLogEntries
            .AsNoTracking()
            .OrderByDescending(e => e.OccurredAt)
            .Take(15)
            .Select(e => new RecentActivityViewModel
            {
                Description = e.Description,
                Timestamp = e.OccurredAt.ToDateTimeUtc(),
                Type = e.Action.ToString()
            })
            .ToListAsync();

        var viewModel = new AdminDashboardViewModel
        {
            TotalMembers = totalMembers,
            ActiveMembers = await _dbContext.Profiles.CountAsync(p => !p.IsSuspended),
            PendingVolunteers = pendingVolunteers,
            PendingApplications = pendingApplications,
            PendingConsents = pendingConsents,
            RecentActivity = recentActivity
        };

        return View(viewModel);
    }

    [HttpGet("Humans")]
    public async Task<IActionResult> Humans(string? search, string? filter, int page = 1)
    {
        var pageSize = 20;
        var query = _dbContext.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(u =>
                u.Email!.Contains(search) ||
                u.DisplayName.Contains(search));
        }

        if (string.Equals(filter, "pending", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(u => u.Profile != null && !u.Profile.IsApproved && !u.Profile.IsSuspended);
        }

        var totalCount = await query.CountAsync();

        var members = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new AdminHumanViewModel
            {
                Id = u.Id,
                Email = u.Email ?? string.Empty,
                DisplayName = u.DisplayName,
                ProfilePictureUrl = u.ProfilePictureUrl,
                CreatedAt = u.CreatedAt.ToDateTimeUtc(),
                LastLoginAt = u.LastLoginAt != null ? u.LastLoginAt.Value.ToDateTimeUtc() : null,
                HasProfile = u.Profile != null,
                IsApproved = u.Profile != null && u.Profile.IsApproved,
                MembershipStatus = u.Profile != null
                    ? (u.Profile.IsSuspended ? "Suspended" : (!u.Profile.IsApproved ? "Pending Approval" : "Active"))
                    : "Inactive"
            })
            .ToListAsync();

        var viewModel = new AdminHumanListViewModel
        {
            Humans = members,
            SearchTerm = search,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View(viewModel);
    }

    [HttpGet("Humans/{id}")]
    public async Task<IActionResult> HumanDetail(Guid id)
    {
        var user = await _dbContext.Users
            .Include(u => u.Profile)
            .Include(u => u.Applications)
            .Include(u => u.ConsentRecords)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null)
        {
            return NotFound();
        }

        var now = _clock.GetCurrentInstant();
        var roleAssignments = await _dbContext.RoleAssignments
            .Include(ra => ra.CreatedByUser)
            .Where(ra => ra.UserId == id)
            .OrderByDescending(ra => ra.ValidFrom)
            .ToListAsync();

        // Query audit entries where the user is either the primary or related entity
        var auditEntries = await _dbContext.AuditLogEntries
            .AsNoTracking()
            .Where(e =>
                (e.EntityType == "User" && e.EntityId == id) ||
                (e.RelatedEntityId == id))
            .OrderByDescending(e => e.OccurredAt)
            .Take(50)
            .Select(e => new AuditLogEntryViewModel
            {
                Action = e.Action.ToString(),
                Description = e.Description,
                OccurredAt = e.OccurredAt.ToDateTimeUtc(),
                ActorName = e.ActorName,
                IsSystemAction = e.ActorUserId == null
            })
            .ToListAsync();

        var viewModel = new AdminHumanDetailViewModel
        {
            UserId = user.Id,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            CreatedAt = user.CreatedAt.ToDateTimeUtc(),
            LastLoginAt = user.LastLoginAt?.ToDateTimeUtc(),
            FirstName = user.Profile?.FirstName,
            LastName = user.Profile?.LastName,
            City = user.Profile?.City,
            CountryCode = user.Profile?.CountryCode,
            IsSuspended = user.Profile?.IsSuspended ?? false,
            IsApproved = user.Profile?.IsApproved ?? false,
            HasProfile = user.Profile != null,
            AdminNotes = user.Profile?.AdminNotes,
            EmergencyContactName = user.Profile?.EmergencyContactName,
            EmergencyContactPhone = user.Profile?.EmergencyContactPhone,
            EmergencyContactRelationship = user.Profile?.EmergencyContactRelationship,
            ApplicationCount = user.Applications.Count,
            ConsentCount = user.ConsentRecords.Count,
            Applications = user.Applications
                .OrderByDescending(a => a.SubmittedAt)
                .Take(5)
                .Select(a => new AdminHumanApplicationViewModel
                {
                    Id = a.Id,
                    Status = a.Status.ToString(),
                    SubmittedAt = a.SubmittedAt.ToDateTimeUtc()
                }).ToList(),
            RoleAssignments = roleAssignments.Select(ra => new AdminRoleAssignmentViewModel
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
            AuditLog = auditEntries
        };

        return View(viewModel);
    }

    [HttpGet("Applications")]
    public async Task<IActionResult> Applications(string? status, int page = 1)
    {
        var pageSize = 20;
        var query = _dbContext.Applications
            .Include(a => a.User)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ApplicationStatus>(status, out var statusEnum))
        {
            query = query.Where(a => a.Status == statusEnum);
        }
        else
        {
            // Default: show pending applications
            query = query.Where(a =>
                a.Status == ApplicationStatus.Submitted ||
                a.Status == ApplicationStatus.UnderReview);
        }

        var totalCount = await query.CountAsync();

        var applications = await query
            .OrderBy(a => a.SubmittedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AdminApplicationViewModel
            {
                Id = a.Id,
                UserId = a.UserId,
                UserEmail = a.User.Email ?? string.Empty,
                UserDisplayName = a.User.DisplayName,
                Status = a.Status.ToString(),
                StatusBadgeClass = a.Status.GetBadgeClass(),
                SubmittedAt = a.SubmittedAt.ToDateTimeUtc(),
                MotivationPreview = a.Motivation.Length > 100 ? a.Motivation.Substring(0, 100) + "..." : a.Motivation
            })
            .ToListAsync();

        var viewModel = new AdminApplicationListViewModel
        {
            Applications = applications,
            StatusFilter = status,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View(viewModel);
    }

    [HttpGet("Applications/{id}")]
    public async Task<IActionResult> ApplicationDetail(Guid id)
    {
        var application = await _dbContext.Applications
            .Include(a => a.User)
            .Include(a => a.ReviewedByUser)
            .Include(a => a.StateHistory)
                .ThenInclude(h => h.ChangedByUser)
            .FirstOrDefaultAsync(a => a.Id == id);

        if (application == null)
        {
            return NotFound();
        }

        var viewModel = new AdminApplicationDetailViewModel
        {
            Id = application.Id,
            UserId = application.UserId,
            UserEmail = application.User.Email ?? string.Empty,
            UserDisplayName = application.User.DisplayName,
            UserProfilePictureUrl = application.User.ProfilePictureUrl,
            Status = application.Status.ToString(),
            Motivation = application.Motivation,
            AdditionalInfo = application.AdditionalInfo,
            Language = application.Language,
            SubmittedAt = application.SubmittedAt.ToDateTimeUtc(),
            ReviewStartedAt = application.ReviewStartedAt?.ToDateTimeUtc(),
            ReviewerName = application.ReviewedByUser?.DisplayName,
            ReviewNotes = application.ReviewNotes,
            CanStartReview = application.Status == ApplicationStatus.Submitted,
            CanApproveReject = application.Status == ApplicationStatus.UnderReview,
            History = application.StateHistory
                .OrderByDescending(h => h.ChangedAt)
                .Select(h => new ApplicationHistoryViewModel
                {
                    Status = h.Status.ToString(),
                    ChangedAt = h.ChangedAt.ToDateTimeUtc(),
                    ChangedBy = h.ChangedByUser.DisplayName,
                    Notes = h.Notes
                }).ToList()
        };

        return View(viewModel);
    }

    [HttpPost("Applications/{id}/Action")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplicationAction(Guid id, AdminApplicationActionModel model)
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null)
        {
            return Unauthorized();
        }

        var application = await _dbContext.Applications
            .Include(a => a.StateHistory)
            .FirstOrDefaultAsync(a => a.Id == id);

        if (application == null)
        {
            return NotFound();
        }

        switch (model.Action.ToUpperInvariant())
        {
            case "STARTREVIEW":
                if (application.Status != ApplicationStatus.Submitted)
                {
                    TempData["ErrorMessage"] = _localizer["Admin_CannotStartReview"].Value;
                    break;
                }
                application.StartReview(currentUser.Id, _clock);
                _logger.LogInformation("Admin {AdminId} started review of application {ApplicationId}",
                    currentUser.Id, application.Id);
                TempData["SuccessMessage"] = _localizer["Admin_ReviewStarted"].Value;
                break;

            case "APPROVE":
                if (application.Status != ApplicationStatus.UnderReview)
                {
                    TempData["ErrorMessage"] = _localizer["Admin_CannotApprove"].Value;
                    break;
                }
                application.Approve(currentUser.Id, model.Notes, _clock);
                _metrics.RecordApplicationProcessed("approved");
                _logger.LogInformation("Admin {AdminId} approved application {ApplicationId}",
                    currentUser.Id, application.Id);
                TempData["SuccessMessage"] = _localizer["Admin_ApplicationApproved"].Value;
                break;

            case "REJECT":
                if (application.Status != ApplicationStatus.UnderReview)
                {
                    TempData["ErrorMessage"] = _localizer["Admin_CannotReject"].Value;
                    break;
                }
                if (string.IsNullOrWhiteSpace(model.Notes))
                {
                    TempData["ErrorMessage"] = _localizer["Admin_ProvideRejectionReason"].Value;
                    break;
                }
                application.Reject(currentUser.Id, model.Notes, _clock);
                _metrics.RecordApplicationProcessed("rejected");
                _logger.LogInformation("Admin {AdminId} rejected application {ApplicationId}",
                    currentUser.Id, application.Id);
                TempData["SuccessMessage"] = _localizer["Admin_ApplicationRejected"].Value;
                break;

            case "REQUESTINFO":
                if (application.Status != ApplicationStatus.UnderReview)
                {
                    TempData["ErrorMessage"] = _localizer["Admin_CannotRequestInfo"].Value;
                    break;
                }
                if (string.IsNullOrWhiteSpace(model.Notes))
                {
                    TempData["ErrorMessage"] = _localizer["Admin_SpecifyInfoNeeded"].Value;
                    break;
                }
                application.RequestMoreInfo(currentUser.Id, model.Notes, _clock);
                _logger.LogInformation("Admin {AdminId} requested more info for application {ApplicationId}",
                    currentUser.Id, application.Id);
                TempData["SuccessMessage"] = _localizer["Admin_MoreInfoRequested"].Value;
                break;

            default:
                TempData["ErrorMessage"] = _localizer["Admin_UnknownAction"].Value;
                break;
        }

        await _dbContext.SaveChangesAsync();
        return RedirectToAction(nameof(ApplicationDetail), new { id });
    }

    [HttpPost("Humans/{id}/Suspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SuspendHuman(Guid id, string? notes)
    {
        var user = await _dbContext.Users
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user?.Profile == null)
        {
            return NotFound();
        }

        var currentUser = await _userManager.GetUserAsync(User);

        user.Profile.IsSuspended = true;
        user.Profile.AdminNotes = notes;
        user.Profile.UpdatedAt = _clock.GetCurrentInstant();

        if (currentUser != null)
        {
            await _auditLogService.LogAsync(
                AuditAction.MemberSuspended, "User", id,
                $"{user.DisplayName} suspended by {currentUser.DisplayName}{(string.IsNullOrWhiteSpace(notes) ? "" : $": {notes}")}",
                currentUser.Id, currentUser.DisplayName);
        }

        await _dbContext.SaveChangesAsync();

        _metrics.RecordMemberSuspended("admin");
        _logger.LogInformation("Admin {AdminId} suspended member {MemberId}", currentUser?.Id, id);

        TempData["SuccessMessage"] = _localizer["Admin_MemberSuspended"].Value;
        return RedirectToAction(nameof(HumanDetail), new { id });
    }

    [HttpPost("Humans/{id}/Unsuspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnsuspendHuman(Guid id)
    {
        var user = await _dbContext.Users
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user?.Profile == null)
        {
            return NotFound();
        }

        var currentUser = await _userManager.GetUserAsync(User);

        user.Profile.IsSuspended = false;
        user.Profile.UpdatedAt = _clock.GetCurrentInstant();

        if (currentUser != null)
        {
            await _auditLogService.LogAsync(
                AuditAction.MemberUnsuspended, "User", id,
                $"{user.DisplayName} unsuspended by {currentUser.DisplayName}",
                currentUser.Id, currentUser.DisplayName);
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Admin {AdminId} unsuspended member {MemberId}", currentUser?.Id, id);

        TempData["SuccessMessage"] = _localizer["Admin_MemberUnsuspended"].Value;
        return RedirectToAction(nameof(HumanDetail), new { id });
    }

    [HttpPost("Humans/{id}/Approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveVolunteer(Guid id)
    {
        var user = await _dbContext.Users
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user?.Profile == null)
        {
            return NotFound();
        }

        var currentUser = await _userManager.GetUserAsync(User);
        var now = _clock.GetCurrentInstant();

        user.Profile.IsApproved = true;
        user.Profile.UpdatedAt = now;

        if (currentUser != null)
        {
            await _auditLogService.LogAsync(
                AuditAction.VolunteerApproved, "User", id,
                $"{user.DisplayName} approved as volunteer by {currentUser.DisplayName}",
                currentUser.Id, currentUser.DisplayName);
        }

        await _dbContext.SaveChangesAsync();

        // Sync Volunteers team membership (adds user if they also have all required consents)
        await _systemTeamSyncJob.SyncVolunteersMembershipForUserAsync(id);

        _metrics.RecordVolunteerApproved();
        _logger.LogInformation("Admin {AdminId} approved volunteer {MemberId}", currentUser?.Id, id);

        TempData["SuccessMessage"] = _localizer["Admin_VolunteerApproved"].Value;
        return RedirectToAction(nameof(HumanDetail), new { id });
    }

    [HttpGet("Teams")]
    public async Task<IActionResult> Teams(int page = 1)
    {
        var pageSize = 20;
        var query = _dbContext.Teams
            .Include(t => t.Members.Where(m => m.LeftAt == null))
            .Include(t => t.JoinRequests.Where(r => r.Status == TeamJoinRequestStatus.Pending))
            .OrderBy(t => t.SystemTeamType)
            .ThenBy(t => t.Name);

        var totalCount = await query.CountAsync();

        var teams = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var viewModel = new AdminTeamListViewModel
        {
            Teams = teams.Select(t => new AdminTeamViewModel
            {
                Id = t.Id,
                Name = t.Name,
                Slug = t.Slug,
                IsActive = t.IsActive,
                RequiresApproval = t.RequiresApproval,
                IsSystemTeam = t.IsSystemTeam,
                SystemTeamType = t.SystemTeamType != SystemTeamType.None ? t.SystemTeamType.ToString() : null,
                MemberCount = t.Members.Count,
                PendingRequestCount = t.JoinRequests.Count,
                CreatedAt = t.CreatedAt.ToDateTimeUtc()
            }).ToList(),
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View(viewModel);
    }

    [HttpGet("Teams/Create")]
    public IActionResult CreateTeam()
    {
        return View(new CreateTeamViewModel());
    }

    [HttpPost("Teams/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTeam(CreateTeamViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        try
        {
            var team = await _teamService.CreateTeamAsync(model.Name, model.Description, model.RequiresApproval);
            var currentUser = await _userManager.GetUserAsync(User);
            _logger.LogInformation("Admin {AdminId} created team {TeamId} ({TeamName})", currentUser?.Id, team.Id, team.Name);

            TempData["SuccessMessage"] = string.Format(_localizer["Admin_TeamCreated"].Value, team.Name);
            return RedirectToAction(nameof(Teams));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating team");
            ModelState.AddModelError("", _localizer["Admin_TeamCreateError"].Value);
            return View(model);
        }
    }

    [HttpGet("Teams/{id}/Edit")]
    public async Task<IActionResult> EditTeam(Guid id)
    {
        var team = await _dbContext.Teams.FindAsync(id);
        if (team == null)
        {
            return NotFound();
        }

        var viewModel = new EditTeamViewModel
        {
            Id = team.Id,
            Name = team.Name,
            Description = team.Description,
            RequiresApproval = team.RequiresApproval,
            IsActive = team.IsActive,
            IsSystemTeam = team.IsSystemTeam
        };

        return View(viewModel);
    }

    [HttpPost("Teams/{id}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditTeam(Guid id, EditTeamViewModel model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        try
        {
            await _teamService.UpdateTeamAsync(id, model.Name, model.Description, model.RequiresApproval, model.IsActive);
            var currentUser = await _userManager.GetUserAsync(User);
            _logger.LogInformation("Admin {AdminId} updated team {TeamId}", currentUser?.Id, id);

            TempData["SuccessMessage"] = _localizer["Admin_TeamUpdated"].Value;
            return RedirectToAction(nameof(Teams));
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return View(model);
        }
    }

    [HttpPost("Teams/{id}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTeam(Guid id)
    {
        try
        {
            await _teamService.DeleteTeamAsync(id);
            var currentUser = await _userManager.GetUserAsync(User);
            _logger.LogInformation("Admin {AdminId} deactivated team {TeamId}", currentUser?.Id, id);

            TempData["SuccessMessage"] = _localizer["Admin_TeamDeactivated"].Value;
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Teams));
    }

    [HttpGet("Roles")]
    public async Task<IActionResult> Roles(string? role, bool showInactive = false, int page = 1)
    {
        var pageSize = 50;
        var now = _clock.GetCurrentInstant();

        var query = _dbContext.RoleAssignments
            .Include(ra => ra.User)
            .Include(ra => ra.CreatedByUser)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(role))
        {
            query = query.Where(ra => ra.RoleName == role);
        }

        if (!showInactive)
        {
            query = query.Where(ra => ra.ValidFrom <= now && (ra.ValidTo == null || ra.ValidTo > now));
        }

        var totalCount = await query.CountAsync();

        var assignments = await query
            .OrderBy(ra => ra.RoleName)
            .ThenByDescending(ra => ra.ValidFrom)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var viewModel = new AdminRoleAssignmentListViewModel
        {
            RoleAssignments = assignments.Select(ra => new AdminRoleAssignmentViewModel
            {
                Id = ra.Id,
                UserId = ra.UserId,
                UserEmail = ra.User.Email ?? string.Empty,
                UserDisplayName = ra.User.DisplayName,
                RoleName = ra.RoleName,
                ValidFrom = ra.ValidFrom.ToDateTimeUtc(),
                ValidTo = ra.ValidTo?.ToDateTimeUtc(),
                Notes = ra.Notes,
                IsActive = ra.IsActive(now),
                CreatedByName = ra.CreatedByUser?.DisplayName,
                CreatedAt = ra.CreatedAt.ToDateTimeUtc()
            }).ToList(),
            RoleFilter = role,
            ShowInactive = showInactive,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View(viewModel);
    }

    [HttpGet("Humans/{id}/Roles/Add")]
    public async Task<IActionResult> AddRole(Guid id)
    {
        var user = await _dbContext.Users.FindAsync(id);
        if (user == null)
        {
            return NotFound();
        }

        var viewModel = new CreateRoleAssignmentViewModel
        {
            UserId = id,
            UserDisplayName = user.DisplayName,
            AvailableRoles = User.IsInRole(RoleNames.Admin)
                ? [RoleNames.Admin, RoleNames.Board]
                : [RoleNames.Board]
        };

        return View(viewModel);
    }

    [HttpPost("Humans/{id}/Roles/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddRole(Guid id, CreateRoleAssignmentViewModel model)
    {
        var user = await _dbContext.Users.FindAsync(id);
        if (user == null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(model.RoleName))
        {
            ModelState.AddModelError(nameof(model.RoleName), "Please select a role.");
            model.UserId = id;
            model.UserDisplayName = user.DisplayName;
            model.AvailableRoles = User.IsInRole(RoleNames.Admin)
                ? [RoleNames.Admin, RoleNames.Board]
                : [RoleNames.Board];
            return View(model);
        }

        // Enforce role assignment authorization
        if (!CanManageRole(model.RoleName))
        {
            return Forbid();
        }

        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null)
        {
            return Unauthorized();
        }

        var now = _clock.GetCurrentInstant();

        // Prevent overlapping role windows at application layer.
        // This also blocks creating a "current" open-ended assignment
        // when a future assignment for the same role already exists.
        var hasOverlap = await _roleAssignmentService.HasOverlappingAssignmentAsync(
            id, model.RoleName, now);

        if (hasOverlap)
        {
            TempData["ErrorMessage"] = string.Format(_localizer["Admin_RoleAlreadyActive"].Value, model.RoleName);
            return RedirectToAction(nameof(HumanDetail), new { id });
        }

        var roleAssignment = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = id,
            RoleName = model.RoleName,
            ValidFrom = now,
            Notes = model.Notes,
            CreatedAt = now,
            CreatedByUserId = currentUser.Id
        };

        _dbContext.RoleAssignments.Add(roleAssignment);

        await _auditLogService.LogAsync(
            AuditAction.RoleAssigned, "User", id,
            $"Role '{model.RoleName}' assigned to {user.DisplayName}",
            currentUser.Id, currentUser.DisplayName);

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Admin {AdminId} assigned role {Role} to user {UserId}",
            currentUser.Id, model.RoleName, id);

        // Trigger sync for Board role changes
        if (string.Equals(model.RoleName, RoleNames.Board, StringComparison.Ordinal))
        {
            await _systemTeamSyncJob.SyncBoardTeamAsync();
        }

        TempData["SuccessMessage"] = string.Format(_localizer["Admin_RoleAssigned"].Value, model.RoleName);
        return RedirectToAction(nameof(HumanDetail), new { id });
    }

    [HttpPost("Roles/{id}/End")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EndRole(Guid id, string? notes)
    {
        var roleAssignment = await _dbContext.RoleAssignments
            .Include(ra => ra.User)
            .FirstOrDefaultAsync(ra => ra.Id == id);

        if (roleAssignment == null)
        {
            return NotFound();
        }

        // Enforce role assignment authorization
        if (!CanManageRole(roleAssignment.RoleName))
        {
            return Forbid();
        }

        var now = _clock.GetCurrentInstant();

        if (!roleAssignment.IsActive(now))
        {
            TempData["ErrorMessage"] = _localizer["Admin_RoleNotActive"].Value;
            return RedirectToAction(nameof(HumanDetail), new { id = roleAssignment.UserId });
        }

        var currentUser = await _userManager.GetUserAsync(User);

        roleAssignment.ValidTo = now;
        if (!string.IsNullOrWhiteSpace(notes))
        {
            roleAssignment.Notes = string.IsNullOrEmpty(roleAssignment.Notes)
                ? $"Ended: {notes}"
                : $"{roleAssignment.Notes} | Ended: {notes}";
        }

        if (currentUser != null)
        {
            await _auditLogService.LogAsync(
                AuditAction.RoleEnded, "User", roleAssignment.UserId,
                $"Role '{roleAssignment.RoleName}' ended for {roleAssignment.User.DisplayName}",
                currentUser.Id, currentUser.DisplayName);
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Admin {AdminId} ended role {Role} for user {UserId}",
            currentUser?.Id, roleAssignment.RoleName, roleAssignment.UserId);

        // Trigger sync for Board role changes
        if (string.Equals(roleAssignment.RoleName, RoleNames.Board, StringComparison.Ordinal))
        {
            await _systemTeamSyncJob.SyncBoardTeamAsync();
        }

        TempData["SuccessMessage"] = string.Format(_localizer["Admin_RoleEnded"].Value, roleAssignment.RoleName, roleAssignment.User.DisplayName);
        return RedirectToAction(nameof(HumanDetail), new { id = roleAssignment.UserId });
    }

    [HttpGet("GoogleSync")]
    public async Task<IActionResult> GoogleSync()
    {
        var preview = await _googleSyncService.PreviewSyncAllAsync();

        var viewModel = new GoogleSyncViewModel
        {
            TotalResources = preview.TotalResources,
            InSyncCount = preview.InSyncCount,
            DriftCount = preview.DriftCount,
            ErrorCount = preview.Diffs.Count(d => d.ErrorMessage != null),
            Resources = preview.Diffs
                .OrderBy(d => d.IsInSync)
                .ThenBy(d => d.TeamName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.ResourceName, StringComparer.OrdinalIgnoreCase)
                .Select(d => new GoogleSyncResourceViewModel
                {
                    ResourceId = d.ResourceId,
                    ResourceName = d.ResourceName,
                    ResourceType = d.ResourceType,
                    TeamName = d.TeamName,
                    Url = d.Url,
                    ErrorMessage = d.ErrorMessage,
                    IsInSync = d.IsInSync,
                    MembersToAdd = d.MembersToAdd,
                    MembersToRemove = d.MembersToRemove
                }).ToList()
        };

        return View(viewModel);
    }

    [HttpPost("SyncSystemTeams")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncSystemTeams()
    {
        try
        {
            await _systemTeamSyncJob.ExecuteAsync();
            TempData["SuccessMessage"] = "System teams synced successfully.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync system teams");
            TempData["ErrorMessage"] = $"Sync failed: {ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("GoogleSync/Apply")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GoogleSyncApply()
    {
        var currentUser = await _userManager.GetUserAsync(User);

        try
        {
            await _googleSyncService.SyncAllResourcesAsync();
            _logger.LogInformation("Admin {AdminId} triggered manual Google resource sync", currentUser?.Id);
            TempData["SuccessMessage"] = _localizer["Admin_GoogleSyncSuccess"].Value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual Google resource sync failed");
            TempData["ErrorMessage"] = _localizer["Admin_GoogleSyncFailed"].Value;
        }

        return RedirectToAction(nameof(GoogleSync));
    }

    [HttpGet("AuditLog")]
    public async Task<IActionResult> AuditLog(string? filter, int page = 1)
    {
        var pageSize = 50;
        var query = _dbContext.AuditLogEntries.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter) && Enum.TryParse<AuditAction>(filter, out var actionEnum))
        {
            query = query.Where(e => e.Action == actionEnum);
        }

        var totalCount = await query.CountAsync();

        var entries = await query
            .OrderByDescending(e => e.OccurredAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new AuditLogEntryViewModel
            {
                Action = e.Action.ToString(),
                Description = e.Description,
                OccurredAt = e.OccurredAt.ToDateTimeUtc(),
                ActorName = e.ActorName,
                IsSystemAction = e.ActorUserId == null
            })
            .ToListAsync();

        var anomalyCount = await _dbContext.AuditLogEntries
            .AsNoTracking()
            .CountAsync(e => e.Action == AuditAction.AnomalousPermissionDetected);

        var viewModel = new AuditLogListViewModel
        {
            Entries = entries,
            ActionFilter = filter,
            AnomalyCount = anomalyCount,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View(viewModel);
    }

    [HttpPost("AuditLog/CheckDriveActivity")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckDriveActivity(
        [FromServices] IDriveActivityMonitorService monitorService)
    {
        var currentUser = await _userManager.GetUserAsync(User);

        try
        {
            var count = await monitorService.CheckForAnomalousActivityAsync();
            _logger.LogInformation("Admin {AdminId} triggered manual Drive activity check: {Count} anomalies",
                currentUser?.Id, count);

            TempData["SuccessMessage"] = count > 0
                ? $"Drive activity check completed: {count} anomalous change(s) detected."
                : "Drive activity check completed: no anomalies detected.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual Drive activity check failed");
            TempData["ErrorMessage"] = "Drive activity check failed. Check logs for details.";
        }

        return RedirectToAction(nameof(AuditLog), new { filter = nameof(AuditAction.AnomalousPermissionDetected) });
    }

    [HttpGet("GoogleSync/Resource/{id}/Audit")]
    public async Task<IActionResult> GoogleSyncResourceAudit(Guid id)
    {
        var resource = await _dbContext.GoogleResources
            .AsNoTracking()
            .Include(r => r.Team)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (resource == null)
        {
            return NotFound();
        }

        var entries = await _auditLogService.GetByResourceAsync(id);

        var viewModel = new GoogleSyncAuditListViewModel
        {
            Title = $"Sync Audit: {resource.Name}",
            BackUrl = Url.Action(nameof(GoogleSync)),
            BackLabel = "Back to Google Sync",
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
                RelatedEntityId = e.RelatedEntityId
            }).ToList()
        };

        return View("GoogleSyncAudit", viewModel);
    }

    [HttpGet("Humans/{id}/GoogleSyncAudit")]
    public async Task<IActionResult> HumanGoogleSyncAudit(Guid id)
    {
        var user = await _dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id);

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

    [HttpGet("Configuration")]
    public IActionResult Configuration([FromServices] IConfiguration configuration)
    {
        var keys = new (string Section, string Key, bool Required)[]
        {
            ("Authentication", "Authentication:Google:ClientId", true),
            ("Authentication", "Authentication:Google:ClientSecret", true),
            ("Database", "ConnectionStrings:DefaultConnection", true),
            ("Email", "Email:SmtpHost", true),
            ("Email", "Email:Username", true),
            ("Email", "Email:Password", true),
            ("Email", "Email:FromAddress", true),
            ("Email", "Email:BaseUrl", true),
            ("Google Workspace", "GoogleWorkspace:ServiceAccountKeyPath", false),
            ("Google Workspace", "GoogleWorkspace:ServiceAccountKeyJson", false),
            ("Google Workspace", "GoogleWorkspace:Domain", false),
            ("GitHub", "GitHub:Owner", true),
            ("GitHub", "GitHub:Repository", true),
            ("GitHub", "GitHub:AccessToken", true),
            ("Google Maps", "GoogleMaps:ApiKey", true),
            ("OpenTelemetry", "OpenTelemetry:OtlpEndpoint", false),
        };

        var items = keys.Select(k =>
        {
            var value = configuration[k.Key];
            var isSet = !string.IsNullOrEmpty(value);
            string preview = "(not set)";
            if (isSet)
            {
                preview = value![..Math.Min(3, value!.Length)] + "...";
            }

            return new ConfigurationItemViewModel
            {
                Section = k.Section,
                Key = k.Key,
                IsSet = isSet,
                Preview = preview,
                IsRequired = k.Required,
            };
        }).ToList();

        return View(new AdminConfigurationViewModel { Items = items });
    }
    [HttpGet("EmailPreview")]
    public IActionResult EmailPreview([FromServices] IOptions<EmailSettings> emailSettings)
    {
        var settings = emailSettings.Value;
        var cultures = new[] { "en", "es", "de", "fr", "it" };

        var sampleName = "Maria Garc\u00eda";
        var sampleEmail = "maria@example.com";
        var sampleDocs = new[] { "Volunteer Agreement", "Privacy Policy" };

        var previews = new Dictionary<string, List<EmailPreviewItem>>(StringComparer.Ordinal);

        foreach (var culture in cultures)
        {
            var ci = new System.Globalization.CultureInfo(culture);
            var prev = System.Globalization.CultureInfo.CurrentUICulture;
            System.Globalization.CultureInfo.CurrentUICulture = ci;

            try
            {
                previews[culture] = GenerateEmailPreviews(settings, sampleName, sampleEmail, sampleDocs);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentUICulture = prev;
            }
        }

        return View(new EmailPreviewViewModel { Previews = previews });
    }

    private List<EmailPreviewItem> GenerateEmailPreviews(EmailSettings settings, string name, string email, string[] docs)
    {
        string Encode(string s) => System.Net.WebUtility.HtmlEncode(s);
        var baseUrl = settings.BaseUrl;

        return
        [
            new()
            {
                Id = "application-submitted",
                Name = "Application Submitted (to Admin)",
                Recipient = settings.AdminAddress,
                Subject = string.Format(_localizer["Email_ApplicationSubmitted_Subject"].Value, name),
                Body = $"""
                    <h2>New Membership Application</h2>
                    <p>A new membership application has been submitted.</p>
                    <ul>
                        <li><strong>Applicant:</strong> {Encode(name)}</li>
                        <li><strong>Application ID:</strong> {Guid.Empty}</li>
                    </ul>
                    <p><a href="{baseUrl}/Admin/Applications">Review Application</a></p>
                    """
            },
            new()
            {
                Id = "application-approved",
                Name = "Application Approved",
                Recipient = email,
                Subject = _localizer["Email_ApplicationApproved_Subject"].Value,
                Body = $"""
                    <h2>{_localizer["Email_ApplicationApproved_Heading"].Value}</h2>
                    <p>Dear {Encode(name)},</p>
                    <p>We're delighted to inform you that your membership application has been approved.
                    Welcome to Humans!</p>
                    <p>You can now access your member profile and explore teams:</p>
                    <ul>
                        <li><a href="{baseUrl}/Profile">View Your Profile</a></li>
                        <li><a href="{baseUrl}/Teams">Browse Teams</a></li>
                        <li><a href="{baseUrl}/Consent">Review Legal Documents</a></li>
                    </ul>
                    <p>If you have any questions, don't hesitate to reach out.</p>
                    <p>Welcome aboard!<br/>The Humans Team</p>
                    """
            },
            new()
            {
                Id = "application-rejected",
                Name = "Application Rejected",
                Recipient = email,
                Subject = _localizer["Email_ApplicationRejected_Subject"].Value,
                Body = $"""
                    <h2>Application Update</h2>
                    <p>Dear {Encode(name)},</p>
                    <p>Thank you for your interest in joining us. After careful review,
                    we regret to inform you that we are unable to approve your membership application at this time.</p>
                    <p><strong>Reason:</strong> Incomplete profile information</p>
                    <p>If you have any questions or would like to discuss this decision,
                    please contact us at <a href="mailto:{settings.AdminAddress}">{settings.AdminAddress}</a>.</p>
                    <p>Best regards,<br/>The Humans Team</p>
                    """
            },
            new()
            {
                Id = "reconsent-required",
                Name = "Re-Consent Required (single doc)",
                Recipient = email,
                Subject = string.Format(_localizer["Email_ReConsentRequired_Subject_Single"].Value, docs[0]),
                Body = $"""
                    <h2>Legal Document Update</h2>
                    <p>Dear {Encode(name)},</p>
                    <p>We have updated the following required documents:</p>
                    <ul>
                        <li><strong>{Encode(docs[0])}</strong></li>
                    </ul>
                    <p>As a member, you need to review and accept these updated documents to maintain your active membership status.</p>
                    <p><a href="{baseUrl}/Consent">Review and Accept</a></p>
                    <p>If you have any questions about the changes, please contact us.</p>
                    <p>Thank you,<br/>The Humans Team</p>
                    """
            },
            new()
            {
                Id = "reconsents-required",
                Name = "Re-Consents Required (multiple docs)",
                Recipient = email,
                Subject = _localizer["Email_ReConsentRequired_Subject_Multiple"].Value,
                Body = $"""
                    <h2>Legal Document Update</h2>
                    <p>Dear {Encode(name)},</p>
                    <p>We have updated the following required documents:</p>
                    <ul>
                        {string.Join("\n", docs.Select(d => $"<li><strong>{Encode(d)}</strong></li>"))}
                    </ul>
                    <p>As a member, you need to review and accept these updated documents to maintain your active membership status.</p>
                    <p><a href="{baseUrl}/Consent">Review and Accept</a></p>
                    <p>If you have any questions about the changes, please contact us.</p>
                    <p>Thank you,<br/>The Humans Team</p>
                    """
            },
            new()
            {
                Id = "reconsent-reminder",
                Name = "Re-Consent Reminder",
                Recipient = email,
                Subject = string.Format(System.Globalization.CultureInfo.CurrentCulture, _localizer["Email_ReConsentReminder_Subject"].Value, 14),
                Body = $"""
                    <h2>Consent Reminder</h2>
                    <p>Dear {Encode(name)},</p>
                    <p>This is a reminder that you have <strong>14 days</strong> remaining to review and accept
                    the following updated documents:</p>
                    <ul>
                        {string.Join("\n", docs.Select(d => $"<li>{Encode(d)}</li>"))}
                    </ul>
                    <p>If you do not accept these documents before the deadline, your membership access may be temporarily suspended.</p>
                    <p><a href="{baseUrl}/Consent">Review Documents Now</a></p>
                    <p>Thank you,<br/>The Humans Team</p>
                    """
            },
            new()
            {
                Id = "welcome",
                Name = "Welcome",
                Recipient = email,
                Subject = _localizer["Email_Welcome_Subject"].Value,
                Body = $"""
                    <h2>{_localizer["Email_Welcome_Heading"].Value}</h2>
                    <p>Dear {Encode(name)},</p>
                    <p>Welcome to the Humans member portal!</p>
                    <p>Here's what you can do:</p>
                    <ul>
                        <li><a href="{baseUrl}/Profile">Complete your profile</a></li>
                        <li><a href="{baseUrl}/Teams">Join teams and working groups</a></li>
                        <li><a href="{baseUrl}/Consent">Review legal documents</a></li>
                    </ul>
                    <p>If you have any questions, feel free to reach out to us.</p>
                    <p>Best regards,<br/>The Humans Team</p>
                    """
            },
            new()
            {
                Id = "access-suspended",
                Name = "Access Suspended",
                Recipient = email,
                Subject = _localizer["Email_AccessSuspended_Subject"].Value,
                Body = $"""
                    <h2>Access Suspended</h2>
                    <p>Dear {Encode(name)},</p>
                    <p>Your membership access has been temporarily suspended.</p>
                    <p><strong>Reason:</strong> Outstanding consent requirements</p>
                    <p>To restore your access, please take the required action:</p>
                    <ul>
                        <li><a href="{baseUrl}/Consent">Review pending consent requirements</a></li>
                    </ul>
                    <p>If you believe this is an error or have questions, please contact us at
                    <a href="mailto:{settings.AdminAddress}">{settings.AdminAddress}</a>.</p>
                    <p>The Humans Team</p>
                    """
            },
            new()
            {
                Id = "email-verification",
                Name = "Email Verification",
                Recipient = "preferred@example.com",
                Subject = _localizer["Email_VerifyEmail_Subject"].Value,
                Body = $"""
                    <h2>Email Verification</h2>
                    <p>Dear {Encode(name)},</p>
                    <p>You requested to set <strong>preferred@example.com</strong> as your preferred email address.</p>
                    <p>Please click the link below to verify this email address:</p>
                    <p><a href="{baseUrl}/Profile/VerifyEmail?token=sample-token">Verify Email Address</a></p>
                    <p>This link will expire in 24 hours.</p>
                    <p>If you did not request this change, you can safely ignore this email.</p>
                    <p>The Humans Team</p>
                    """
            },
            new()
            {
                Id = "deletion-requested",
                Name = "Account Deletion Requested",
                Recipient = email,
                Subject = _localizer["Email_DeletionRequested_Subject"].Value,
                Body = $"""
                    <h2>Account Deletion Request Received</h2>
                    <p>Dear {Encode(name)},</p>
                    <p>We have received your request to delete your account. Your account and all associated data
                    will be permanently deleted on <strong>March 15, 2026</strong>.</p>
                    <p>If you change your mind, you can cancel this request before the deletion date by visiting:</p>
                    <p><a href="{baseUrl}/Profile/Privacy">Cancel Deletion Request</a></p>
                    <p>After deletion, this action cannot be undone and all your data will be permanently removed.</p>
                    <p>The Humans Team</p>
                    """
            },
            new()
            {
                Id = "account-deleted",
                Name = "Account Deleted",
                Recipient = email,
                Subject = _localizer["Email_AccountDeleted_Subject"].Value,
                Body = $"""
                    <h2>Account Deleted</h2>
                    <p>Dear {Encode(name)},</p>
                    <p>As requested, your Humans account has been permanently deleted.
                    All your personal data has been removed from our systems.</p>
                    <p>Thank you for being part of our community. If you ever wish to rejoin,
                    you're welcome to submit a new membership application.</p>
                    <p>Best wishes,<br/>The Humans Team</p>
                    """
            },
            new()
            {
                Id = "added-to-team",
                Name = "Added to Team",
                Recipient = email,
                Subject = string.Format(_localizer["Email_AddedToTeam_Subject"].Value, "Art Collective"),
                Body = $"""
                    <h2>Welcome to Art Collective!</h2>
                    <p>Dear {Encode(name)},</p>
                    <p>You have been added to the <strong>Art Collective</strong> team.</p>
                    <p>Your team has the following resources:</p>
                    <ul>
                        <li><a href="https://drive.google.com/drive/folders/example">Art Collective Shared Drive</a></li>
                        <li><a href="https://groups.google.com/g/art-collective">art-collective@nobodies.team</a></li>
                    </ul>
                    <p><a href="{baseUrl}/Teams/art-collective">View Team Page</a></p>
                    <p>The Humans Team</p>
                    """
            }
        ];
    }

    // Intentionally anonymous: exposes only migration names and counts (no sensitive data).
    // Used by dev tooling to check which migrations have been applied in QA/prod,
    // so old migrations can be safely squashed and removed from the repo.
    [HttpGet("DbVersion")]
    [AllowAnonymous]
    [Produces("application/json")]
    public async Task<IActionResult> DbVersion()
    {
        var applied = (await _dbContext.Database.GetAppliedMigrationsAsync()).ToList();
        var pending = await _dbContext.Database.GetPendingMigrationsAsync();

        return Ok(new
        {
            lastApplied = applied.LastOrDefault(),
            appliedCount = applied.Count,
            pendingCount = pending.Count()
        });
    }

    /// <summary>
    /// Checks whether the current user can assign/end the specified role.
    /// Admin can manage any role. Board can manage Board and Lead only.
    /// </summary>
    private bool CanManageRole(string roleName)
    {
        if (User.IsInRole(RoleNames.Admin))
        {
            return true;
        }

        // Board members can manage the Board role
        if (User.IsInRole(RoleNames.Board))
        {
            return string.Equals(roleName, RoleNames.Board, StringComparison.Ordinal);
        }

        return false;
    }
}
