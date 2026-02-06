using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Profiles.Application.Interfaces;
using Profiles.Domain.Constants;
using Profiles.Domain.Entities;
using Profiles.Domain.Enums;
using Profiles.Infrastructure.Data;
using Profiles.Infrastructure.Jobs;
using Profiles.Web.Extensions;
using Profiles.Web.Models;
using MemberApplication = Profiles.Domain.Entities.Application;

namespace Profiles.Web.Controllers;

[Authorize(Roles = "Board,Admin")]
[Route("Admin")]
public class AdminController : Controller
{
    private readonly ProfilesDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly ITeamService _teamService;
    private readonly IClock _clock;
    private readonly ILogger<AdminController> _logger;
    private readonly SystemTeamSyncJob _systemTeamSyncJob;

    public AdminController(
        ProfilesDbContext dbContext,
        UserManager<User> userManager,
        ITeamService teamService,
        IClock clock,
        ILogger<AdminController> logger,
        SystemTeamSyncJob systemTeamSyncJob)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _teamService = teamService;
        _clock = clock;
        _logger = logger;
        _systemTeamSyncJob = systemTeamSyncJob;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var totalMembers = await _dbContext.Users.CountAsync();
        var pendingApplications = await _dbContext.Applications
            .CountAsync(a => a.Status == ApplicationStatus.Submitted || a.Status == ApplicationStatus.UnderReview);

        var viewModel = new AdminDashboardViewModel
        {
            TotalMembers = totalMembers,
            ActiveMembers = await _dbContext.Profiles.CountAsync(p => !p.IsSuspended),
            PendingApplications = pendingApplications,
            PendingConsents = 0, // Would calculate from consent requirements
            RecentActivity = []
        };

        return View(viewModel);
    }

    [HttpGet("Members")]
    public async Task<IActionResult> Members(string? search, int page = 1)
    {
        var pageSize = 20;
        var query = _dbContext.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(u =>
                u.Email!.Contains(search) ||
                u.DisplayName.Contains(search));
        }

        var totalCount = await query.CountAsync();

        var members = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new AdminMemberViewModel
            {
                Id = u.Id,
                Email = u.Email ?? string.Empty,
                DisplayName = u.DisplayName,
                ProfilePictureUrl = u.ProfilePictureUrl,
                CreatedAt = u.CreatedAt.ToDateTimeUtc(),
                LastLoginAt = u.LastLoginAt != null ? u.LastLoginAt.Value.ToDateTimeUtc() : null,
                HasProfile = u.Profile != null,
                MembershipStatus = u.Profile != null && !u.Profile.IsSuspended ? "Active" : "Inactive"
            })
            .ToListAsync();

        var viewModel = new AdminMemberListViewModel
        {
            Members = members,
            SearchTerm = search,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View(viewModel);
    }

    [HttpGet("Members/{id}")]
    public async Task<IActionResult> MemberDetail(Guid id)
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

        var viewModel = new AdminMemberDetailViewModel
        {
            UserId = user.Id,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            CreatedAt = user.CreatedAt.ToDateTimeUtc(),
            LastLoginAt = user.LastLoginAt?.ToDateTimeUtc(),
            FirstName = user.Profile?.FirstName,
            LastName = user.Profile?.LastName,
            PhoneNumber = user.Profile?.PhoneNumber,
            City = user.Profile?.City,
            CountryCode = user.Profile?.CountryCode,
            IsSuspended = user.Profile?.IsSuspended ?? false,
            AdminNotes = user.Profile?.AdminNotes,
            ApplicationCount = user.Applications.Count,
            ConsentCount = user.ConsentRecords.Count,
            Applications = user.Applications
                .OrderByDescending(a => a.SubmittedAt)
                .Take(5)
                .Select(a => new AdminMemberApplicationViewModel
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
            }).ToList()
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
                    ChangedBy = h.ChangedByUser?.DisplayName ?? "System",
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
                    TempData["ErrorMessage"] = "Cannot start review - application is not in Submitted status.";
                    break;
                }
                application.StartReview(currentUser.Id, _clock);
                _logger.LogInformation("Admin {AdminId} started review of application {ApplicationId}",
                    currentUser.Id, application.Id);
                TempData["SuccessMessage"] = "Review started.";
                break;

            case "APPROVE":
                if (application.Status != ApplicationStatus.UnderReview)
                {
                    TempData["ErrorMessage"] = "Cannot approve - application is not under review.";
                    break;
                }
                application.Approve(currentUser.Id, model.Notes, _clock);
                _logger.LogInformation("Admin {AdminId} approved application {ApplicationId}",
                    currentUser.Id, application.Id);
                TempData["SuccessMessage"] = "Application approved!";
                break;

            case "REJECT":
                if (application.Status != ApplicationStatus.UnderReview)
                {
                    TempData["ErrorMessage"] = "Cannot reject - application is not under review.";
                    break;
                }
                if (string.IsNullOrWhiteSpace(model.Notes))
                {
                    TempData["ErrorMessage"] = "Please provide a reason for rejection.";
                    break;
                }
                application.Reject(currentUser.Id, model.Notes, _clock);
                _logger.LogInformation("Admin {AdminId} rejected application {ApplicationId}",
                    currentUser.Id, application.Id);
                TempData["SuccessMessage"] = "Application rejected.";
                break;

            case "REQUESTINFO":
                if (application.Status != ApplicationStatus.UnderReview)
                {
                    TempData["ErrorMessage"] = "Cannot request info - application is not under review.";
                    break;
                }
                if (string.IsNullOrWhiteSpace(model.Notes))
                {
                    TempData["ErrorMessage"] = "Please specify what information is needed.";
                    break;
                }
                application.RequestMoreInfo(currentUser.Id, model.Notes, _clock);
                _logger.LogInformation("Admin {AdminId} requested more info for application {ApplicationId}",
                    currentUser.Id, application.Id);
                TempData["SuccessMessage"] = "More information requested.";
                break;

            default:
                TempData["ErrorMessage"] = "Unknown action.";
                break;
        }

        await _dbContext.SaveChangesAsync();
        return RedirectToAction(nameof(ApplicationDetail), new { id });
    }

    [HttpPost("Members/{id}/Suspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SuspendMember(Guid id, string? notes)
    {
        var user = await _dbContext.Users
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user?.Profile == null)
        {
            return NotFound();
        }

        user.Profile.IsSuspended = true;
        user.Profile.AdminNotes = notes;
        user.Profile.UpdatedAt = _clock.GetCurrentInstant();

        await _dbContext.SaveChangesAsync();

        var currentUser = await _userManager.GetUserAsync(User);
        _logger.LogInformation("Admin {AdminId} suspended member {MemberId}", currentUser?.Id, id);

        TempData["SuccessMessage"] = "Member suspended.";
        return RedirectToAction(nameof(MemberDetail), new { id });
    }

    [HttpPost("Members/{id}/Unsuspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnsuspendMember(Guid id)
    {
        var user = await _dbContext.Users
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user?.Profile == null)
        {
            return NotFound();
        }

        user.Profile.IsSuspended = false;
        user.Profile.UpdatedAt = _clock.GetCurrentInstant();

        await _dbContext.SaveChangesAsync();

        var currentUser = await _userManager.GetUserAsync(User);
        _logger.LogInformation("Admin {AdminId} unsuspended member {MemberId}", currentUser?.Id, id);

        TempData["SuccessMessage"] = "Member unsuspended.";
        return RedirectToAction(nameof(MemberDetail), new { id });
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

            TempData["SuccessMessage"] = $"Team '{team.Name}' created successfully.";
            return RedirectToAction(nameof(Teams));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating team");
            ModelState.AddModelError("", "An error occurred while creating the team.");
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

            TempData["SuccessMessage"] = "Team updated successfully.";
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

            TempData["SuccessMessage"] = "Team deactivated successfully.";
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

    [HttpGet("Members/{id}/Roles/Add")]
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
                ? [RoleNames.Admin, RoleNames.Board, RoleNames.Metalead]
                : [RoleNames.Board, RoleNames.Metalead]
        };

        return View(viewModel);
    }

    [HttpPost("Members/{id}/Roles/Add")]
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
                ? [RoleNames.Admin, RoleNames.Board, RoleNames.Metalead]
                : [RoleNames.Board, RoleNames.Metalead];
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

        // Check if user already has an active assignment for this role
        var existingActive = await _dbContext.RoleAssignments
            .AnyAsync(ra => ra.UserId == id
                && ra.RoleName == model.RoleName
                && ra.ValidFrom <= now
                && (ra.ValidTo == null || ra.ValidTo > now));

        if (existingActive)
        {
            TempData["ErrorMessage"] = $"User already has an active {model.RoleName} role.";
            return RedirectToAction(nameof(MemberDetail), new { id });
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
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Admin {AdminId} assigned role {Role} to user {UserId}",
            currentUser.Id, model.RoleName, id);

        // Trigger sync for Board role changes
        if (string.Equals(model.RoleName, RoleNames.Board, StringComparison.Ordinal))
        {
            await _systemTeamSyncJob.SyncBoardTeamAsync();
        }

        TempData["SuccessMessage"] = $"Role '{model.RoleName}' assigned successfully.";
        return RedirectToAction(nameof(MemberDetail), new { id });
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
            TempData["ErrorMessage"] = "This role assignment is not currently active.";
            return RedirectToAction(nameof(MemberDetail), new { id = roleAssignment.UserId });
        }

        roleAssignment.ValidTo = now;
        if (!string.IsNullOrWhiteSpace(notes))
        {
            roleAssignment.Notes = string.IsNullOrEmpty(roleAssignment.Notes)
                ? $"Ended: {notes}"
                : $"{roleAssignment.Notes} | Ended: {notes}";
        }

        await _dbContext.SaveChangesAsync();

        var currentUser = await _userManager.GetUserAsync(User);
        _logger.LogInformation("Admin {AdminId} ended role {Role} for user {UserId}",
            currentUser?.Id, roleAssignment.RoleName, roleAssignment.UserId);

        // Trigger sync for Board role changes
        if (string.Equals(roleAssignment.RoleName, RoleNames.Board, StringComparison.Ordinal))
        {
            await _systemTeamSyncJob.SyncBoardTeamAsync();
        }

        TempData["SuccessMessage"] = $"Role '{roleAssignment.RoleName}' ended for {roleAssignment.User.DisplayName}.";
        return RedirectToAction(nameof(MemberDetail), new { id = roleAssignment.UserId });
    }

    /// <summary>
    /// Checks whether the current user can assign/end the specified role.
    /// Admin can manage any role. Board can manage Board and Metalead only.
    /// </summary>
    private bool CanManageRole(string roleName)
    {
        if (User.IsInRole(RoleNames.Admin))
        {
            return true;
        }

        // Board members can manage Board and Metalead roles
        if (User.IsInRole(RoleNames.Board))
        {
            return string.Equals(roleName, RoleNames.Board, StringComparison.Ordinal)
                || string.Equals(roleName, RoleNames.Metalead, StringComparison.Ordinal);
        }

        return false;
    }
}
