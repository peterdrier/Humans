using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Application.DTOs;
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
    private readonly IEmailService _emailService;
    private readonly IClock _clock;
    private readonly ILogger<AdminController> _logger;
    private readonly SystemTeamSyncJob _systemTeamSyncJob;
    private readonly HumansMetricsService _metrics;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IWebHostEnvironment _environment;

    public AdminController(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        ITeamService teamService,
        IGoogleSyncService googleSyncService,
        IAuditLogService auditLogService,
        IMembershipCalculator membershipCalculator,
        IRoleAssignmentService roleAssignmentService,
        IEmailService emailService,
        IClock clock,
        ILogger<AdminController> logger,
        SystemTeamSyncJob systemTeamSyncJob,
        HumansMetricsService metrics,
        IStringLocalizer<SharedResource> localizer,
        IWebHostEnvironment environment)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _teamService = teamService;
        _googleSyncService = googleSyncService;
        _auditLogService = auditLogService;
        _membershipCalculator = membershipCalculator;
        _roleAssignmentService = roleAssignmentService;
        _emailService = emailService;
        _clock = clock;
        _logger = logger;
        _systemTeamSyncJob = systemTeamSyncJob;
        _metrics = metrics;
        _localizer = localizer;
        _environment = environment;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var totalMembers = await _dbContext.Users.CountAsync();
        var pendingApplications = await _dbContext.Applications
            .CountAsync(a => a.Status == ApplicationStatus.Submitted);
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

        // Application statistics (non-withdrawn)
        var appStats = await _dbContext.Applications
            .AsNoTracking()
            .Where(a => a.Status != ApplicationStatus.Withdrawn)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Approved = g.Count(a => a.Status == ApplicationStatus.Approved),
                Rejected = g.Count(a => a.Status == ApplicationStatus.Rejected),
                Colaborador = g.Count(a => a.MembershipTier == MembershipTier.Colaborador),
                Asociado = g.Count(a => a.MembershipTier == MembershipTier.Asociado)
            })
            .FirstOrDefaultAsync();

        var viewModel = new AdminDashboardViewModel
        {
            TotalMembers = totalMembers,
            ActiveMembers = await _dbContext.Profiles.CountAsync(p => !p.IsSuspended),
            PendingVolunteers = pendingVolunteers,
            PendingApplications = pendingApplications,
            PendingConsents = pendingConsents,
            RecentActivity = recentActivity,
            TotalApplications = appStats?.Total ?? 0,
            ApprovedApplications = appStats?.Approved ?? 0,
            RejectedApplications = appStats?.Rejected ?? 0,
            ColaboradorApplied = appStats?.Colaborador ?? 0,
            AsociadoApplied = appStats?.Asociado ?? 0
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

        // Load rejected-by user name if present
        string? rejectedByName = null;
        if (user.Profile?.RejectedByUserId != null)
        {
            var rejectedByUser = await _dbContext.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == user.Profile.RejectedByUserId.Value);
            rejectedByName = rejectedByUser?.DisplayName;
        }

        var viewModel = new AdminHumanDetailViewModel
        {
            UserId = user.Id,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            CreatedAt = user.CreatedAt.ToDateTimeUtc(),
            LastLoginAt = user.LastLoginAt?.ToDateTimeUtc(),
            IsSuspended = user.Profile?.IsSuspended ?? false,
            IsApproved = user.Profile?.IsApproved ?? false,
            HasProfile = user.Profile != null,
            AdminNotes = user.Profile?.AdminNotes,
            MembershipTier = user.Profile?.MembershipTier ?? MembershipTier.Volunteer,
            ConsentCheckStatus = user.Profile?.ConsentCheckStatus,
            IsRejected = user.Profile?.RejectedAt != null,
            RejectionReason = user.Profile?.RejectionReason,
            RejectedAt = user.Profile?.RejectedAt?.ToDateTimeUtc(),
            RejectedByName = rejectedByName,
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
    public async Task<IActionResult> Applications(string? status, string? tier, int page = 1)
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
                a.Status == ApplicationStatus.Submitted);
        }

        if (!string.IsNullOrWhiteSpace(tier) && Enum.TryParse<MembershipTier>(tier, out var tierEnum))
        {
            query = query.Where(a => a.MembershipTier == tierEnum);
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
                MotivationPreview = a.Motivation.Length > 100 ? a.Motivation.Substring(0, 100) + "..." : a.Motivation,
                MembershipTier = a.MembershipTier.ToString()
            })
            .ToListAsync();

        var viewModel = new AdminApplicationListViewModel
        {
            Applications = applications,
            StatusFilter = status,
            TierFilter = tier,
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
            SignificantContribution = application.SignificantContribution,
            RoleUnderstanding = application.RoleUnderstanding,
            MembershipTier = application.MembershipTier,
            Language = application.Language,
            SubmittedAt = application.SubmittedAt.ToDateTimeUtc(),
            ReviewStartedAt = application.ReviewStartedAt?.ToDateTimeUtc(),
            ReviewerName = application.ReviewedByUser?.DisplayName,
            ReviewNotes = application.ReviewNotes,
            CanApproveReject = application.Status == ApplicationStatus.Submitted,
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
        _logger.LogInformation("Admin {AdminId} suspended human {HumanId}", currentUser?.Id, id);

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

        _logger.LogInformation("Admin {AdminId} unsuspended human {HumanId}", currentUser?.Id, id);

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
        _logger.LogInformation("Admin {AdminId} approved human {HumanId}", currentUser?.Id, id);

        TempData["SuccessMessage"] = _localizer["Admin_VolunteerApproved"].Value;
        return RedirectToAction(nameof(HumanDetail), new { id });
    }

    [HttpPost("Humans/{id}/Reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectSignup(Guid id, string? reason)
    {
        var user = await _dbContext.Users
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user?.Profile == null)
        {
            return NotFound();
        }

        if (user.Profile.RejectedAt != null)
        {
            TempData["ErrorMessage"] = "This human has already been rejected.";
            return RedirectToAction(nameof(HumanDetail), new { id });
        }

        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null)
        {
            return Unauthorized();
        }

        var now = _clock.GetCurrentInstant();

        user.Profile.RejectionReason = reason;
        user.Profile.RejectedAt = now;
        user.Profile.RejectedByUserId = currentUser.Id;
        user.Profile.IsApproved = false;
        user.Profile.UpdatedAt = now;

        await _auditLogService.LogAsync(
            AuditAction.SignupRejected, "User", id,
            $"{user.DisplayName} rejected by {currentUser.DisplayName}{(string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}")}",
            currentUser.Id, currentUser.DisplayName);

        await _dbContext.SaveChangesAsync();

        try
        {
            await _emailService.SendSignupRejectedAsync(
                user.Email ?? string.Empty,
                user.DisplayName,
                reason,
                user.PreferredLanguage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send signup rejection email to {Email}", user.Email);
        }

        _logger.LogInformation("Admin {AdminId} rejected signup for human {HumanId}", currentUser.Id, id);

        TempData["SuccessMessage"] = "Signup rejected.";
        return RedirectToAction(nameof(HumanDetail), new { id });
    }

    [HttpPost("Humans/{id}/Purge")]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> PurgeHuman(Guid id)
    {
        if (_environment.IsProduction())
        {
            return NotFound();
        }

        var user = await _dbContext.Users.FindAsync(id);

        if (user == null)
        {
            return NotFound();
        }

        var currentUser = await _userManager.GetUserAsync(User);

        if (user.Id == currentUser?.Id)
        {
            TempData["ErrorMessage"] = "You cannot purge your own account.";
            return RedirectToAction(nameof(HumanDetail), new { id });
        }

        var displayName = user.DisplayName;

        _logger.LogWarning(
            "Admin {AdminId} purging human {HumanId} ({DisplayName}) in {Environment}",
            currentUser?.Id, id, displayName, _environment.EnvironmentName);

        // Sever OAuth link so next Google login creates a fresh user
        var logins = await _userManager.GetLoginsAsync(user);
        foreach (var login in logins)
        {
            await _userManager.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey);
        }

        // Remove UserEmails so the unique index doesn't block the new account
        var userEmails = await _dbContext.UserEmails.Where(e => e.UserId == id).ToListAsync();
        _dbContext.UserEmails.RemoveRange(userEmails);

        // Change email so email-based lookup won't match
        var purgedEmail = $"purged-{Guid.NewGuid()}@deleted.local";
        user.Email = purgedEmail;
        user.NormalizedEmail = purgedEmail.ToUpperInvariant();
        user.UserName = purgedEmail;
        user.NormalizedUserName = purgedEmail.ToUpperInvariant();
        user.DisplayName = $"Purged ({displayName})";

        // Lock out the account permanently
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;

        await _dbContext.SaveChangesAsync();

        _logger.LogWarning("Purged human {DisplayName} ({HumanId})", displayName, id);

        TempData["SuccessMessage"] = $"Purged {displayName}. They will get a fresh account on next login.";
        return RedirectToAction(nameof(Humans));
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
                ? [RoleNames.Admin, RoleNames.Board, RoleNames.ConsentCoordinator, RoleNames.VolunteerCoordinator]
                : [RoleNames.Board, RoleNames.ConsentCoordinator, RoleNames.VolunteerCoordinator]
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
                ? [RoleNames.Admin, RoleNames.Board, RoleNames.ConsentCoordinator, RoleNames.VolunteerCoordinator]
                : [RoleNames.Board, RoleNames.ConsentCoordinator, RoleNames.VolunteerCoordinator];
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
    public IActionResult EmailPreview(
        [FromServices] IEmailRenderer renderer,
        [FromServices] IOptions<EmailSettings> emailSettings)
    {
        var settings = emailSettings.Value;
        var cultures = new[] { "en", "es", "de", "fr", "it" };

        // Per-locale persona stubs for realistic previews
        var personas = new Dictionary<string, (string Name, string Email)>(StringComparer.Ordinal)
        {
            ["en"] = ("Sally Smith", "sally@example.com"),
            ["es"] = ("Mar\u00eda Garc\u00eda", "maria@example.com"),
            ["de"] = ("Frieda Fischer", "frieda@example.com"),
            ["fr"] = ("Fran\u00e7ois Dupont", "francois@example.com"),
            ["it"] = ("Giulia Rossi", "giulia@example.com"),
        };

        var sampleDocs = new[] { "Volunteer Agreement", "Privacy Policy" };
        var sampleResources = new (string Name, string? Url)[]
        {
            ("Art Collective Shared Drive", "https://drive.google.com/drive/folders/example"),
            ("art-collective@nobodies.team", "https://groups.google.com/g/art-collective"),
        };

        var previews = new Dictionary<string, List<EmailPreviewItem>>(StringComparer.Ordinal);

        foreach (var culture in cultures)
        {
            var (name, email) = personas[culture];

            var items = new List<EmailPreviewItem>();

            var c1 = renderer.RenderApplicationSubmitted(Guid.Empty, name);
            items.Add(new EmailPreviewItem { Id = "application-submitted", Name = "Application Submitted (to Admin)", Recipient = settings.AdminAddress, Subject = c1.Subject, Body = c1.HtmlBody });

            var c2 = renderer.RenderApplicationApproved(name, MembershipTier.Colaborador, culture);
            items.Add(new EmailPreviewItem { Id = "application-approved", Name = "Application Approved", Recipient = email, Subject = c2.Subject, Body = c2.HtmlBody });

            var c3 = renderer.RenderApplicationRejected(name, MembershipTier.Asociado, "Incomplete profile information", culture);
            items.Add(new EmailPreviewItem { Id = "application-rejected", Name = "Application Rejected", Recipient = email, Subject = c3.Subject, Body = c3.HtmlBody });

            var c4 = renderer.RenderSignupRejected(name, "Incomplete profile information", culture);
            items.Add(new EmailPreviewItem { Id = "signup-rejected", Name = "Signup Rejected", Recipient = email, Subject = c4.Subject, Body = c4.HtmlBody });

            var c5 = renderer.RenderReConsentsRequired(name, new[] { sampleDocs[0] }, culture);
            items.Add(new EmailPreviewItem { Id = "reconsent-required", Name = "Re-Consent Required (single doc)", Recipient = email, Subject = c5.Subject, Body = c5.HtmlBody });

            var c6 = renderer.RenderReConsentsRequired(name, sampleDocs, culture);
            items.Add(new EmailPreviewItem { Id = "reconsents-required", Name = "Re-Consents Required (multiple docs)", Recipient = email, Subject = c6.Subject, Body = c6.HtmlBody });

            var c7 = renderer.RenderReConsentReminder(name, sampleDocs, 14, culture);
            items.Add(new EmailPreviewItem { Id = "reconsent-reminder", Name = "Re-Consent Reminder", Recipient = email, Subject = c7.Subject, Body = c7.HtmlBody });

            var c8 = renderer.RenderWelcome(name, culture);
            items.Add(new EmailPreviewItem { Id = "welcome", Name = "Welcome", Recipient = email, Subject = c8.Subject, Body = c8.HtmlBody });

            var c9 = renderer.RenderAccessSuspended(name, "Outstanding consent requirements", culture);
            items.Add(new EmailPreviewItem { Id = "access-suspended", Name = "Access Suspended", Recipient = email, Subject = c9.Subject, Body = c9.HtmlBody });

            var c10 = renderer.RenderEmailVerification(name, "preferred@example.com", $"{settings.BaseUrl}/Profile/VerifyEmail?token=sample-token", culture);
            items.Add(new EmailPreviewItem { Id = "email-verification", Name = "Email Verification", Recipient = "preferred@example.com", Subject = c10.Subject, Body = c10.HtmlBody });

            var c11 = renderer.RenderAccountDeletionRequested(name, "March 15, 2026", culture);
            items.Add(new EmailPreviewItem { Id = "deletion-requested", Name = "Account Deletion Requested", Recipient = email, Subject = c11.Subject, Body = c11.HtmlBody });

            var c12 = renderer.RenderAccountDeleted(name, culture);
            items.Add(new EmailPreviewItem { Id = "account-deleted", Name = "Account Deleted", Recipient = email, Subject = c12.Subject, Body = c12.HtmlBody });

            var c13 = renderer.RenderAddedToTeam(name, "Art Collective", "art-collective", sampleResources, culture);
            items.Add(new EmailPreviewItem { Id = "added-to-team", Name = "Added to Team", Recipient = email, Subject = c13.Subject, Body = c13.HtmlBody });

            var c14 = renderer.RenderTermRenewalReminder(name, "Colaborador", "April 1, 2026", culture);
            items.Add(new EmailPreviewItem { Id = "term-renewal-reminder", Name = "Term Renewal Reminder", Recipient = email, Subject = c14.Subject, Body = c14.HtmlBody });

            var sampleDigestGroups = new List<BoardDigestTierGroup>
            {
                new("Volunteer", new[] { "Alice Johnson", "Bob Smith" }),
                new("Colaborador", new[] { "Carlos García" })
            };
            var sampleOutstanding = new BoardDigestOutstandingCounts(
                OnboardingReview: 3,
                StillOnboarding: 5,
                BoardVotingTotal: 7,
                BoardVotingYours: 4,
                TeamJoinRequests: 2,
                PendingConsents: 12,
                PendingDeletions: 1);
            var c15 = renderer.RenderBoardDailyDigest(name, "2026-02-22", sampleDigestGroups, sampleOutstanding, culture);
            items.Add(new EmailPreviewItem { Id = "board-daily-digest", Name = "Board Daily Digest", Recipient = email, Subject = c15.Subject, Body = c15.HtmlBody });

            previews[culture] = items;
        }

        return View(new EmailPreviewViewModel { Previews = previews });
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
    /// Admin can manage any role. Board can manage Board and coordinator roles.
    /// </summary>
    private bool CanManageRole(string roleName)
    {
        if (User.IsInRole(RoleNames.Admin))
        {
            return true;
        }

        // Board members can manage Board and coordinator roles
        if (User.IsInRole(RoleNames.Board))
        {
            return string.Equals(roleName, RoleNames.Board, StringComparison.Ordinal) ||
                   string.Equals(roleName, RoleNames.ConsentCoordinator, StringComparison.Ordinal) ||
                   string.Equals(roleName, RoleNames.VolunteerCoordinator, StringComparison.Ordinal);
        }

        return false;
    }
}
