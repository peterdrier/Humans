using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Humans.Application.DTOs;
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

[Authorize(Roles = RoleNames.Admin)]
[Route("Google")]
public class GoogleController : HumansControllerBase
{
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IAuditLogService _auditLogService;
    private readonly ITeamResourceService _teamResourceService;
    private readonly IGoogleWorkspaceUserService _workspaceUserService;
    private readonly IUserEmailService _userEmailService;
    private readonly IEmailProvisioningService _emailProvisioningService;
    private readonly HumansDbContext _dbContext;
    private readonly ILogger<GoogleController> _logger;

    private const string NobodiesTeamDomain = "nobodies.team";

    public GoogleController(
        UserManager<User> userManager,
        IGoogleSyncService googleSyncService,
        IAuditLogService auditLogService,
        ITeamResourceService teamResourceService,
        IGoogleWorkspaceUserService workspaceUserService,
        IUserEmailService userEmailService,
        IEmailProvisioningService emailProvisioningService,
        HumansDbContext dbContext,
        ILogger<GoogleController> logger)
        : base(userManager)
    {
        _googleSyncService = googleSyncService;
        _auditLogService = auditLogService;
        _teamResourceService = teamResourceService;
        _workspaceUserService = workspaceUserService;
        _userEmailService = userEmailService;
        _emailProvisioningService = emailProvisioningService;
        _dbContext = dbContext;
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
            ViewBag.Teams = await _dbContext.Teams.Where(t => t.IsActive).OrderBy(t => t.Name).ToListAsync();
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

        try
        {
            var team = await _dbContext.Teams.FindAsync(teamId);
            if (team is null) return NotFound();

            var normalizedPrefix = groupPrefix.Trim().ToLowerInvariant();
            var previousPrefix = team.GoogleGroupPrefix;
            team.GoogleGroupPrefix = normalizedPrefix;

            var linkResult = await _googleSyncService.EnsureTeamGroupAsync(teamId);
            if (linkResult.RequiresConfirmation)
            {
                await _dbContext.SaveChangesAsync();
                SetInfo($"Linked group for team \"{team.Name}\". Note: {linkResult.WarningMessage}");
            }
            else if (linkResult.ErrorMessage is not null)
            {
                team.GoogleGroupPrefix = previousPrefix;
                SetError($"Could not link group: {linkResult.ErrorMessage}");
            }
            else
            {
                await _dbContext.SaveChangesAsync();
                SetSuccess($"Successfully linked {normalizedPrefix}@nobodies.team to team \"{team.Name}\".");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to link group {GroupPrefix} to team {TeamId}", groupPrefix, teamId);
            SetError($"Failed to link group: {ex.Message}");
        }

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

        var updated = 0;
        var errors = new List<string>();

        foreach (var userId in selectedUserIds)
        {
            if (!corrections.TryGetValue(userId.ToString(), out var googleEmail) || string.IsNullOrEmpty(googleEmail))
                continue;

            try
            {
                var user = await _dbContext.Users
                    .Include(u => u.UserEmails)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (user is null)
                {
                    errors.Add($"User {userId} not found.");
                    continue;
                }

                var oldEmail = user.Email;

                user.Email = googleEmail;
                user.UserName = googleEmail;
                user.NormalizedEmail = googleEmail.ToUpperInvariant();
                user.NormalizedUserName = googleEmail.ToUpperInvariant();

                // Update OAuth UserEmail record if it exists
                var oauthEmail = user.UserEmails.FirstOrDefault(e => e.IsOAuth);
                if (oauthEmail is not null)
                {
                    oauthEmail.Email = googleEmail;
                }

                _logger.LogInformation(
                    "Admin {AdminId} applying email backfill for user {UserId}: '{OldEmail}' -> '{NewEmail}'",
                    currentUser.Id, userId, oldEmail, googleEmail);

                updated++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update email for user {UserId}", userId);
                errors.Add($"Error updating user {userId}: {ex.Message}");
            }
        }

        if (updated > 0)
            await _dbContext.SaveChangesAsync();

        if (errors.Count > 0)
            SetError($"Applied {updated} correction(s) with {errors.Count} error(s): {string.Join("; ", errors)}");
        else
            SetSuccess($"Applied {updated} email correction(s) successfully.");

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
        var result = await _googleSyncService.SyncSingleResourceAsync(resourceId, SyncAction.Execute);
        return Json(result);
    }

    [HttpPost("Sync/ExecuteAll/{resourceType}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncExecuteAll(GoogleResourceType resourceType)
    {
        var result = await _googleSyncService.SyncResourcesByTypeAsync(resourceType, SyncAction.Execute);
        return Json(result);
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
            id, emailPrefix, currentUser.Id, currentUser.DisplayName);

        if (!result.Success)
        {
            SetError(result.ErrorMessage ?? "Provisioning failed.");
        }
        else if (result.RecoveryEmail is not null)
        {
            SetSuccess($"Account {result.FullEmail} provisioned and linked. Credentials sent to {result.RecoveryEmail}.");
        }
        else
        {
            SetSuccess($"Account {result.FullEmail} provisioned and linked. No recovery email found — credentials not sent.");
        }

        return RedirectToAction("AdminDetail", "Profile", new { id });
    }

    // --- Workspace Accounts (from AdminEmailController) ---

    [HttpGet("Accounts")]
    public async Task<IActionResult> Accounts()
    {
        try
        {
            var accounts = await _workspaceUserService.ListAccountsAsync();

            // Load all user emails to match accounts to humans
            var allUserEmails = await _dbContext.UserEmails
                .AsNoTracking()
                .Include(ue => ue.User)
                .ToListAsync();

            var accountViewModels = new List<WorkspaceEmailAccountViewModel>();
            var notPrimaryCount = 0;

            foreach (var account in accounts)
            {
                var matchedEmail = allUserEmails.FirstOrDefault(ue =>
                    string.Equals(ue.Email, account.PrimaryEmail, StringComparison.OrdinalIgnoreCase));

                var isUsedAsPrimary = matchedEmail is { IsNotificationTarget: true };

                // Count accounts that exist in the system but are not used as primary
                if (matchedEmail is not null && !isUsedAsPrimary)
                {
                    notPrimaryCount++;
                }

                accountViewModels.Add(new WorkspaceEmailAccountViewModel
                {
                    PrimaryEmail = account.PrimaryEmail,
                    FirstName = account.FirstName,
                    LastName = account.LastName,
                    IsSuspended = account.IsSuspended,
                    CreationTime = account.CreationTime,
                    LastLoginTime = account.LastLoginTime,
                    MatchedUserId = matchedEmail?.UserId,
                    MatchedDisplayName = matchedEmail?.User?.DisplayName,
                    IsUsedAsPrimary = isUsedAsPrimary
                });
            }

            var linkedCount = accountViewModels.Count(a => a.MatchedUserId.HasValue);

            var model = new WorkspaceEmailListViewModel
            {
                Accounts = accountViewModels
                    .OrderBy(a => a.PrimaryEmail, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                TotalAccounts = accountViewModels.Count,
                ActiveAccounts = accountViewModels.Count(a => !a.IsSuspended),
                SuspendedAccounts = accountViewModels.Count(a => a.IsSuspended),
                LinkedAccounts = linkedCount,
                UnlinkedAccounts = accountViewModels.Count - linkedCount,
                NotPrimaryCount = notPrimaryCount
            };

            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load @nobodies.team accounts");
            SetError("Failed to load @nobodies.team accounts. Check the logs for details.");
            return View(new WorkspaceEmailListViewModel());
        }
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

        var emailPrefix = model.EmailPrefix.Trim().ToLowerInvariant();
        var fullEmail = $"{emailPrefix}@{NobodiesTeamDomain}";

        // Check if account already exists
        var existing = await _workspaceUserService.GetAccountAsync(fullEmail);
        if (existing is not null)
        {
            SetError($"Account {fullEmail} already exists.");
            return RedirectToAction(nameof(Accounts));
        }

        try
        {
            // Generate a temporary password
            var tempPassword = PasswordGenerator.GenerateTemporary();

            await _workspaceUserService.ProvisionAccountAsync(
                fullEmail, model.FirstName.Trim(), model.LastName.Trim(), tempPassword);

            var currentUser = await GetCurrentUserAsync();
            if (currentUser is not null)
            {
                await _auditLogService.LogAsync(
                    AuditAction.WorkspaceAccountProvisioned,
                    "WorkspaceAccount", Guid.Empty,
                    $"Provisioned @{NobodiesTeamDomain} account: {fullEmail}",
                    currentUser.Id, currentUser.DisplayName);
                await _dbContext.SaveChangesAsync();
            }

            SetSuccess($"Account {fullEmail} provisioned. Temporary password: {tempPassword}");
            return RedirectToAction(nameof(Accounts));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision @nobodies.team account: {Email}", fullEmail);
            SetError($"Failed to provision {fullEmail}. Check logs for details.");
            return RedirectToAction(nameof(Accounts));
        }
    }

    [HttpPost("Accounts/Suspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SuspendAccount(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest();
        }

        try
        {
            await _workspaceUserService.SuspendAccountAsync(email);

            var currentUser = await GetCurrentUserAsync();
            if (currentUser is not null)
            {
                await _auditLogService.LogAsync(
                    AuditAction.WorkspaceAccountSuspended,
                    "WorkspaceAccount", Guid.Empty,
                    $"Suspended @{NobodiesTeamDomain} account: {email}",
                    currentUser.Id, currentUser.DisplayName);
                await _dbContext.SaveChangesAsync();
            }

            SetSuccess($"Account {email} suspended.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to suspend account: {Email}", email);
            SetError($"Failed to suspend {email}.");
        }

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

        try
        {
            await _workspaceUserService.ReactivateAccountAsync(email);

            var currentUser = await GetCurrentUserAsync();
            if (currentUser is not null)
            {
                await _auditLogService.LogAsync(
                    AuditAction.WorkspaceAccountReactivated,
                    "WorkspaceAccount", Guid.Empty,
                    $"Reactivated @{NobodiesTeamDomain} account: {email}",
                    currentUser.Id, currentUser.DisplayName);
                await _dbContext.SaveChangesAsync();
            }

            SetSuccess($"Account {email} reactivated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reactivate account: {Email}", email);
            SetError($"Failed to reactivate {email}.");
        }

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

        try
        {
            var newPassword = PasswordGenerator.GenerateTemporary();
            await _workspaceUserService.ResetPasswordAsync(email, newPassword);

            var currentUser = await GetCurrentUserAsync();
            if (currentUser is not null)
            {
                await _auditLogService.LogAsync(
                    AuditAction.WorkspaceAccountPasswordReset,
                    "WorkspaceAccount", Guid.Empty,
                    $"Reset password for @{NobodiesTeamDomain} account: {email}",
                    currentUser.Id, currentUser.DisplayName);
                await _dbContext.SaveChangesAsync();
            }

            SetSuccess($"Password reset for {email}. New temporary password: {newPassword}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset password for: {Email}", email);
            SetError($"Failed to reset password for {email}.");
        }

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

        try
        {
            var user = await _dbContext.Users.FindAsync(userId);
            if (user is null)
            {
                SetError("Human not found.");
                return RedirectToAction(nameof(Accounts));
            }

            // Check not already linked
            var alreadyLinked = await _dbContext.UserEmails
                .AnyAsync(ue => EF.Functions.ILike(ue.Email, email));
            if (alreadyLinked)
            {
                SetError($"{email} is already linked to a human.");
                return RedirectToAction(nameof(Accounts));
            }

            // Add as verified email (also sets notification target for @nobodies.team)
            await _userEmailService.AddVerifiedEmailAsync(userId, email);

            // Auto-set as Google service email
            user.GoogleEmail = email;
            await _dbContext.SaveChangesAsync();

            // Audit
            var currentUser = await GetCurrentUserAsync();
            if (currentUser is not null)
            {
                await _auditLogService.LogAsync(
                    AuditAction.WorkspaceAccountLinked,
                    "WorkspaceAccount", userId,
                    $"Linked @{NobodiesTeamDomain} account {email} to {user.DisplayName}",
                    currentUser.Id, currentUser.DisplayName);
                await _dbContext.SaveChangesAsync();
            }

            SetSuccess($"Linked {email} to {user.DisplayName}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to link {Email} to user {UserId}", email, userId);
            SetError($"Failed to link {email}.");
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
