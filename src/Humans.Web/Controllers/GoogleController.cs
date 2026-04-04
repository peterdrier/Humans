using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
[Route("Google")]
public class GoogleController : HumansControllerBase
{
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IAuditLogService _auditLogService;
    private readonly ITeamResourceService _teamResourceService;
    private readonly IEmailProvisioningService _emailProvisioningService;
    private readonly IGoogleAdminService _googleAdminService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GoogleController> _logger;

    public GoogleController(
        UserManager<User> userManager,
        IGoogleSyncService googleSyncService,
        IAuditLogService auditLogService,
        ITeamResourceService teamResourceService,
        IEmailProvisioningService emailProvisioningService,
        IGoogleAdminService googleAdminService,
        IMemoryCache cache,
        ILogger<GoogleController> logger)
        : base(userManager)
    {
        _googleSyncService = googleSyncService;
        _auditLogService = auditLogService;
        _teamResourceService = teamResourceService;
        _emailProvisioningService = emailProvisioningService;
        _googleAdminService = googleAdminService;
        _cache = cache;
        _logger = logger;
    }

    // --- Sync Settings (from AdminController) ---

    [HttpGet("SyncSettings")]
    public async Task<IActionResult> SyncSettings([FromServices] ISyncSettingsService syncSettingsService)
    {
        var settings = await syncSettingsService.GetAllAsync();
        var viewModel = new SyncSettingsViewModel
        {
            Settings = settings.Select(s => new SyncServiceSettingViewModel
            {
                ServiceType = s.ServiceType,
                ServiceName = FormatServiceName(s.ServiceType),
                CurrentMode = s.SyncMode,
                UpdatedAt = s.UpdatedAt.ToDateTimeUtc(),
                UpdatedByName = s.UpdatedByUser?.DisplayName
            }).ToList()
        };
        return View(viewModel);
    }

    [HttpPost("SyncSettings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSyncSetting(
        [FromServices] ISyncSettingsService syncSettingsService,
        SyncServiceType serviceType, SyncMode mode)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null) return Unauthorized();

        await syncSettingsService.UpdateModeAsync(serviceType, mode, currentUser.Id);

        _logger.LogInformation("Admin {AdminId} changed {ServiceType} sync mode to {Mode}",
            currentUser.Id, serviceType, mode);

        SetSuccess($"Sync mode for {FormatServiceName(serviceType)} updated to {mode}.");
        return RedirectToAction(nameof(SyncSettings));
    }

    // --- System Team Sync (from AdminController) ---

    [HttpPost("SyncSystemTeams")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncSystemTeams(
        [FromServices] Humans.Infrastructure.Jobs.SystemTeamSyncJob systemTeamSyncJob)
    {
        try
        {
            var report = await systemTeamSyncJob.ExecuteAsync();
            TempData["SyncReport"] = System.Text.Json.JsonSerializer.Serialize(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync system teams");
            SetError($"Sync failed: {ex.Message}");
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction(nameof(SyncResults));
    }

    [HttpGet("SyncResults")]
    public IActionResult SyncResults()
    {
        SyncReport? report = null;
        if (TempData["SyncReport"] is string json)
        {
            report = System.Text.Json.JsonSerializer.Deserialize<SyncReport>(json);
        }

        if (report is null)
        {
            SetInfo("No sync results to display. Run a sync first.");
            return RedirectToAction(nameof(Index));
        }

        return View(report);
    }

    // --- Google Group Settings (from AdminController) ---

    [HttpPost("CheckGroupSettings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckGroupSettings()
    {
        try
        {
            var result = await _googleSyncService.CheckGroupSettingsAsync();
            TempData["GroupSettingsResult"] = System.Text.Json.JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check Google Group settings");
            SetError($"Settings check failed: {ex.Message}");
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction(nameof(GroupSettingsResults));
    }

    [HttpGet("GroupSettingsResults")]
    public IActionResult GroupSettingsResults()
    {
        GroupSettingsDriftResult? result = null;
        if (TempData["GroupSettingsResult"] is string json)
        {
            result = System.Text.Json.JsonSerializer.Deserialize<GroupSettingsDriftResult>(json);
        }

        if (result is null)
        {
            SetInfo("No group settings results to display. Run the check first.");
            return RedirectToAction(nameof(Index));
        }

        return View(result);
    }

    [HttpPost("RemediateGroupSettings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemediateGroupSettings(
        [FromForm] string groupEmail, [FromForm] string? returnUrl)
    {
        try
        {
            var success = await _googleSyncService.RemediateGroupSettingsAsync(groupEmail);
            if (success) SetSuccess($"Settings remediated for {groupEmail}.");
            else SetError($"Remediation skipped for {groupEmail} — sync may be disabled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remediate settings for {GroupEmail}", groupEmail);
            SetError($"Remediation failed for {groupEmail}: {ex.Message}");
        }
        return Redirect(Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Action(nameof(AllGroups))!);
    }

    [HttpPost("RemediateAllGroupSettings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemediateAllGroupSettings()
    {
        try
        {
            var result = await _googleSyncService.GetAllDomainGroupsAsync();
            if (result.ErrorMessage is not null)
            {
                SetError($"Failed to enumerate groups: {result.ErrorMessage}");
                return RedirectToAction(nameof(AllGroups));
            }

            var drifted = result.Groups.Where(g => g.HasDrift).ToList();
            if (drifted.Count == 0)
            {
                SetInfo("No drifted groups found — nothing to remediate.");
                return RedirectToAction(nameof(AllGroups));
            }

            var fixed_ = 0;
            var errors = new List<string>();

            foreach (var group in drifted)
            {
                try
                {
                    var success = await _googleSyncService.RemediateGroupSettingsAsync(group.GroupEmail);
                    if (success) fixed_++;
                    else errors.Add($"{group.GroupEmail}: sync disabled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to remediate {GroupEmail}", group.GroupEmail);
                    errors.Add($"{group.GroupEmail}: {ex.Message}");
                }
            }

            if (errors.Count > 0)
                SetError($"Remediated {fixed_} group(s) with {errors.Count} error(s): {string.Join("; ", errors)}");
            else
                SetSuccess($"Remediated {fixed_} drifted group(s) successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remediate all group settings");
            SetError($"Batch remediation failed: {ex.Message}");
        }
        return RedirectToAction(nameof(AllGroups));
    }

    // --- All Domain Groups (from AdminController) ---

    [HttpGet("AllGroups")]
    public async Task<IActionResult> AllGroups()
    {
        try
        {
            var result = await _googleSyncService.GetAllDomainGroupsAsync();
            var teams = await _googleAdminService.GetActiveTeamsAsync();
            ViewBag.Teams = teams;
            return View(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load domain groups");
            SetError($"Failed to load domain groups: {ex.Message}");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("LinkGroupToTeam")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LinkGroupToTeam(
        [FromForm] Guid teamId, [FromForm] string groupPrefix)
    {
        if (string.IsNullOrWhiteSpace(groupPrefix))
        {
            SetError("Group prefix is required.");
            return RedirectToAction(nameof(AllGroups));
        }

        var result = await _googleAdminService.LinkGroupToTeamAsync(teamId, groupPrefix);

        if (result.ErrorMessage is not null)
            SetError(result.ErrorMessage);
        else if (result.InfoMessage is not null)
            SetInfo(result.InfoMessage);
        else if (result.Message is not null)
            SetSuccess(result.Message);

        return RedirectToAction(nameof(AllGroups));
    }

    // --- Email Backfill (from AdminController) ---

    [HttpPost("CheckEmailMismatches")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckEmailMismatches()
    {
        try
        {
            var result = await _googleSyncService.GetEmailMismatchesAsync();
            TempData["EmailBackfillResult"] = System.Text.Json.JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check email mismatches");
            SetError($"Email mismatch check failed: {ex.Message}");
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction(nameof(EmailBackfillReview));
    }

    [HttpGet("EmailBackfillReview")]
    public IActionResult EmailBackfillReview()
    {
        EmailBackfillResult? result = null;
        if (TempData["EmailBackfillResult"] is string json)
        {
            result = System.Text.Json.JsonSerializer.Deserialize<EmailBackfillResult>(json);
        }

        if (result is null)
        {
            SetInfo("No email mismatch results to display. Run the check first.");
            return RedirectToAction(nameof(Index));
        }

        return View(result);
    }

    [HttpPost("ApplyEmailBackfill")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyEmailBackfill(
        [FromForm] List<Guid> selectedUserIds,
        [FromForm] Dictionary<string, string> corrections)
    {
        if (selectedUserIds.Count == 0)
        {
            SetInfo("No users selected.");
            return RedirectToAction(nameof(Index));
        }

        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null) return Unauthorized();

        var result = await _googleAdminService.ApplyEmailBackfillAsync(
            selectedUserIds, corrections, currentUser.Id);

        if (result.Errors.Count > 0)
            SetError($"Applied {result.UpdatedCount} correction(s) with {result.Errors.Count} error(s): {string.Join("; ", result.Errors)}");
        else
            SetSuccess($"Applied {result.UpdatedCount} email correction(s) successfully.");

        return RedirectToAction(nameof(Index));
    }

    // --- Resource Sync Dashboard (from TeamController) ---

    [HttpGet("Sync")]
    [Authorize(Roles = RoleGroups.TeamsAdminBoardOrAdmin)]
    public IActionResult Sync()
    {
        var viewModel = new TeamSyncViewModel
        {
            CanExecuteActions = RoleChecks.IsAdmin(User)
        };
        return View(viewModel);
    }

    [HttpGet("Sync/Preview/{resourceType}")]
    [Authorize(Roles = RoleGroups.TeamsAdminBoardOrAdmin)]
    public async Task<IActionResult> SyncPreview(GoogleResourceType resourceType)
    {
        var result = await _googleSyncService.SyncResourcesByTypeAsync(resourceType, SyncAction.Preview);

        // Sort resources alphabetically
        result.Diffs.Sort((a, b) =>
            string.Compare(a.ResourceName, b.ResourceName, StringComparison.Ordinal));

        // Sort members within each resource: by state then by displayName
        foreach (var diff in result.Diffs)
        {
            diff.Members.Sort((a, b) =>
            {
                var stateCompare = a.State.CompareTo(b.State);
                return stateCompare != 0
                    ? stateCompare
                    : string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal);
            });
        }

        var viewModel = new SyncTabContentViewModel
        {
            Result = result,
            ResourceType = resourceType.ToString(),
            CanExecuteActions = RoleChecks.IsAdmin(User),
            CanViewAudit = RoleChecks.IsAdminOrBoard(User)
        };

        return PartialView("_SyncTabContent", viewModel);
    }

    [HttpPost("Sync/Execute/{resourceId}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncExecute(Guid resourceId)
    {
        try
        {
            var result = await _googleSyncService.SyncSingleResourceAsync(resourceId, SyncAction.Execute);
            return Json(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute sync for resource {ResourceId}", resourceId);
            return Json(new { ErrorMessage = ex.Message });
        }
    }

    [HttpPost("Sync/ExecuteAll/{resourceType}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncExecuteAll(GoogleResourceType resourceType)
    {
        try
        {
            var result = await _googleSyncService.SyncResourcesByTypeAsync(resourceType, SyncAction.Execute);
            return Json(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute sync for resource type {ResourceType}", resourceType);
            return Json(new { ErrorMessage = ex.Message });
        }
    }

    // --- Drive Activity (from BoardController) ---

    [HttpPost("AuditLog/CheckDriveActivity")]
    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckDriveActivity(
        [FromServices] IDriveActivityMonitorService monitorService)
    {
        var currentUser = await GetCurrentUserAsync();

        try
        {
            var count = await monitorService.CheckForAnomalousActivityAsync();
            _logger.LogInformation("Board {UserId} triggered manual Drive activity check: {Count} anomalies",
                currentUser?.Id, count);

            SetSuccess(count > 0
                ? $"Drive activity check completed: {count} anomalous change(s) detected."
                : "Drive activity check completed: no anomalies detected.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual Drive activity check failed");
            SetError("Drive activity check failed. Check logs for details.");
        }

        return RedirectToAction("AuditLog", "Board", new { filter = nameof(AuditAction.AnomalousPermissionDetected) });
    }

    // --- Sync Audit Views (from BoardController and HumanController) ---

    [HttpGet("Sync/Resource/{id:guid}/Audit")]
    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    public async Task<IActionResult> GoogleSyncResourceAudit(Guid id)
    {
        var resource = await _teamResourceService.GetResourceByIdAsync(id);

        if (resource is null)
        {
            return NotFound();
        }

        var entries = await _auditLogService.GetByResourceAsync(id);
        return GoogleSyncAuditView(
            $"Sync Audit: {resource.Name}",
            Url.Action(nameof(Sync)),
            "Back to Sync Status",
            entries);
    }

    [HttpGet("Human/{id:guid}/SyncAudit")]
    [Authorize(Roles = RoleGroups.HumanAdminBoardOrAdmin)]
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
            Url.Action("AdminDetail", "Profile", new { id }),
            "Back to Member Detail",
            entries);
    }

    // --- Human Email Provisioning (from HumanController) ---

    [HttpPost("Human/{id:guid}/ProvisionEmail")]
    [Authorize(Roles = RoleGroups.HumanAdminOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ProvisionEmail(Guid id, string emailPrefix)
    {
        if (string.IsNullOrWhiteSpace(emailPrefix))
        {
            SetError("Email prefix is required.");
            return RedirectToAction("AdminDetail", "Profile", new { id });
        }

        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
            return NotFound();

        var result = await _emailProvisioningService.ProvisionNobodiesEmailAsync(
            id, emailPrefix, currentUser.Id);

        if (!result.Success)
        {
            SetError(result.ErrorMessage ?? "Provisioning failed.");
        }
        else
        {
            // Evict the nobodies.team email cache so the ViewComponent reflects the new email immediately
            _cache.Remove(ViewComponents.NobodiesEmailBadgeViewComponent.CacheKey);

            if (result.RecoveryEmail is not null)
            {
                SetSuccess($"Account {result.FullEmail} provisioned and linked. Credentials sent to {result.RecoveryEmail}.");
            }
            else
            {
                SetSuccess($"Account {result.FullEmail} provisioned and linked. No recovery email found — credentials not sent.");
            }
        }

        return RedirectToAction("AdminDetail", "Profile", new { id });
    }

    // --- Workspace Accounts (from AdminEmailController) ---

    [HttpGet("Accounts")]
    public async Task<IActionResult> Accounts()
    {
        var result = await _googleAdminService.GetWorkspaceAccountListAsync();

        if (result.ErrorMessage is not null)
        {
            SetError(result.ErrorMessage);
        }

        var model = new WorkspaceEmailListViewModel
        {
            Accounts = result.Accounts.Select(a => new WorkspaceEmailAccountViewModel
            {
                PrimaryEmail = a.PrimaryEmail,
                FirstName = a.FirstName,
                LastName = a.LastName,
                IsSuspended = a.IsSuspended,
                CreationTime = a.CreationTime,
                LastLoginTime = a.LastLoginTime,
                MatchedUserId = a.MatchedUserId,
                MatchedDisplayName = a.MatchedDisplayName,
                IsUsedAsPrimary = a.IsUsedAsPrimary
            }).ToList(),
            TotalAccounts = result.TotalAccounts,
            ActiveAccounts = result.ActiveAccounts,
            SuspendedAccounts = result.SuspendedAccounts,
            LinkedAccounts = result.LinkedAccounts,
            UnlinkedAccounts = result.UnlinkedAccounts,
            NotPrimaryCount = result.NotPrimaryCount
        };

        return View(model);
    }

    [HttpPost("Accounts/Provision")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ProvisionAccount(ProvisionWorkspaceAccountModel model)
    {
        if (string.IsNullOrWhiteSpace(model.EmailPrefix) ||
            string.IsNullOrWhiteSpace(model.FirstName) ||
            string.IsNullOrWhiteSpace(model.LastName))
        {
            SetError("All fields are required.");
            return RedirectToAction(nameof(Accounts));
        }

        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null) return Unauthorized();

        var result = await _googleAdminService.ProvisionStandaloneAccountAsync(
            model.EmailPrefix, model.FirstName, model.LastName,
            currentUser.Id);

        if (result.Success)
            SetSuccess(result.Message!);
        else
            SetError(result.ErrorMessage!);

        return RedirectToAction(nameof(Accounts));
    }

    [HttpPost("Accounts/Suspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SuspendAccount(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest();
        }

        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null) return Unauthorized();

        var result = await _googleAdminService.SuspendAccountAsync(
            email, currentUser.Id);

        if (result.Success)
            SetSuccess(result.Message!);
        else
            SetError(result.ErrorMessage!);

        return RedirectToAction(nameof(Accounts));
    }

    [HttpPost("Accounts/Reactivate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReactivateAccount(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest();
        }

        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null) return Unauthorized();

        var result = await _googleAdminService.ReactivateAccountAsync(
            email, currentUser.Id);

        if (result.Success)
            SetSuccess(result.Message!);
        else
            SetError(result.ErrorMessage!);

        return RedirectToAction(nameof(Accounts));
    }

    [HttpPost("Accounts/ResetPassword")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest();
        }

        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null) return Unauthorized();

        var result = await _googleAdminService.ResetPasswordAsync(
            email, currentUser.Id);

        if (result.Success)
            SetSuccess(result.Message!);
        else
            SetError(result.ErrorMessage!);

        return RedirectToAction(nameof(Accounts));
    }

    [HttpPost("Accounts/Link")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LinkAccount(string email, Guid userId)
    {
        if (string.IsNullOrWhiteSpace(email) || userId == Guid.Empty)
        {
            SetError("Email and human are required.");
            return RedirectToAction(nameof(Accounts));
        }

        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null) return Unauthorized();

        var result = await _googleAdminService.LinkAccountAsync(
            email, userId, currentUser.Id);

        if (result.Success)
        {
            _cache.Remove(ViewComponents.NobodiesEmailBadgeViewComponent.CacheKey);
            SetSuccess(result.Message!);
        }
        else
        {
            SetError(result.ErrorMessage!);
        }

        return RedirectToAction(nameof(Accounts));
    }

    // --- Index ---

    [HttpGet("")]
    public IActionResult Index()
    {
        return View();
    }

    // --- Helpers ---

    private static string FormatServiceName(SyncServiceType type) => type switch
    {
        SyncServiceType.GoogleDrive => "Google Drive",
        SyncServiceType.GoogleGroups => "Google Groups",
        SyncServiceType.Discord => "Discord",
        _ => type.ToString()
    };
}
