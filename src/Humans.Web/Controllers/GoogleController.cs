using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Constants;
using Humans.Web.Models;
using Humans.Web.Models.Google;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Route("Google")]
public class GoogleController(
    IUserServiceRead userService,
    IGoogleSyncService googleSyncService,
    IGoogleGroupSync googleGroupSync,
    IAuditViewerService auditViewer,
    ITeamResourceService teamResourceService,
    IEmailProvisioningService emailProvisioningService,
    IGoogleAdminService googleAdminService,
    ILogger<GoogleController> logger) : HumansControllerBase(userService)
{
    private readonly IAuditViewerService _auditViewer = auditViewer;

    // --- Sync Settings ---

    [HttpGet("SyncSettings")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> SyncSettings(
        [FromServices] ISyncSettingsService syncSettingsService,
        [FromServices] IUserServiceRead userService)
    {
        var settings = await syncSettingsService.GetAllAsync();

        // In-memory join: resolve UpdatedByUser display names via IUserServiceRead
        // rather than an EF .Include across the section boundary (design-rules §6).
        var updatedByUserIds = settings
            .Select(s => s.UpdatedByUserId)
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var updatedByUsers = updatedByUserIds.Count > 0
            ? await userService.GetUserInfosAsync(updatedByUserIds)
            : new Dictionary<Guid, UserInfo>();

        var viewModel = new SyncSettingsViewModel
        {
            Settings = settings.Select(s => new SyncServiceSettingViewModel
            {
                ServiceType = s.ServiceType,
                ServiceName = FormatServiceName(s.ServiceType),
                CurrentMode = s.SyncMode,
                UpdatedAt = s.UpdatedAt.ToDateTimeUtc(),
                UpdatedByName = s.UpdatedByUserId is { } uid && updatedByUsers.TryGetValue(uid, out var u)
                    ? u.BurnerName
                    : null
            }).ToList()
        };
        return View(viewModel);
    }

    [HttpPost("SyncSettings")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSyncSetting(
        [FromServices] ISyncSettingsService syncSettingsService,
        SyncServiceType serviceType, SyncMode mode)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null) return Unauthorized();

        await syncSettingsService.UpdateModeAsync(serviceType, mode, currentUser.Id);

        logger.LogInformation("Admin {AdminId} changed {ServiceType} sync mode to {Mode}",
            currentUser.Id, serviceType, mode);

        SetSuccess($"Sync mode for {FormatServiceName(serviceType)} updated to {mode}.");
        return RedirectToAction(nameof(SyncSettings));
    }

    // --- System Team Sync ---

    [HttpPost("SyncSystemTeams")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncSystemTeams(
        [FromServices] ISystemTeamSync systemTeamSyncJob)
    {
        try
        {
            var report = await systemTeamSyncJob.ExecuteAsync();
            TempData[TempDataKeys.SyncReport] = System.Text.Json.JsonSerializer.Serialize(report);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to sync system teams");
            SetError($"Sync failed: {ex.Message}");
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction(nameof(SyncResults));
    }

    [HttpGet("SyncResults")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public IActionResult SyncResults()
    {
        SyncReport? report = null;
        if (TempData[TempDataKeys.SyncReport] is string json)
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

    // --- Google Group Settings ---

    [HttpPost("CheckGroupSettings")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckGroupSettings()
    {
        try
        {
            var result = await googleSyncService.CheckGroupSettingsAsync();
            TempData[TempDataKeys.GroupSettingsResult] = System.Text.Json.JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to check Google Group settings");
            SetError($"Settings check failed: {ex.Message}");
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction(nameof(GroupSettingsResults));
    }

    [HttpGet("GroupSettingsResults")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public IActionResult GroupSettingsResults()
    {
        GroupSettingsDriftResult? result = null;
        if (TempData[TempDataKeys.GroupSettingsResult] is string json)
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
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemediateGroupSettings(
        [FromForm] string groupEmail, [FromForm] string? returnUrl)
    {
        var result = await googleSyncService.RemediateGroupSettingsAsync(groupEmail);
        if (result.Succeeded)
            SetSuccess($"Settings remediated for {groupEmail}.");
        else
            SetError(result.ErrorMessage ?? $"Remediation failed for {groupEmail}.");

        return Redirect(Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Action(nameof(AllGroups))!);
    }

    [HttpPost("RemediateAllGroupSettings")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemediateAllGroupSettings()
    {
        try
        {
            var result = await googleSyncService.GetAllDomainGroupsAsync();
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
                var remediation = await googleSyncService.RemediateGroupSettingsAsync(group.GroupEmail);
                if (remediation.Succeeded) fixed_++;
                else errors.Add($"{group.GroupEmail}: {remediation.ErrorMessage ?? "failed"}");
            }

            if (errors.Count > 0)
                SetError($"Remediated {fixed_} group(s) with {errors.Count} error(s): {string.Join("; ", errors)}");
            else
                SetSuccess($"Remediated {fixed_} drifted group(s) successfully.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remediate all group settings");
            SetError($"Batch remediation failed: {ex.Message}");
        }
        return RedirectToAction(nameof(AllGroups));
    }

    // --- All Domain Groups ---

    [HttpGet("AllGroups")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> AllGroups()
    {
        try
        {
            var result = await googleSyncService.GetAllDomainGroupsAsync();
            var teams = (await googleAdminService.GetActiveTeamsAsync())
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList();
            ViewBag.Teams = teams;
            return View(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load domain groups");
            SetError($"Failed to load domain groups: {ex.Message}");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("LinkGroupToTeam")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LinkGroupToTeam(
        [FromForm] Guid teamId, [FromForm] string groupPrefix)
    {
        if (string.IsNullOrWhiteSpace(groupPrefix))
        {
            SetError("Group prefix is required.");
            return RedirectToAction(nameof(AllGroups));
        }

        var result = await googleAdminService.LinkGroupToTeamAsync(teamId, groupPrefix);

        if (result.ErrorMessage is not null)
            SetError(result.ErrorMessage);
        else if (result.InfoMessage is not null)
            SetInfo(result.InfoMessage);
        else if (result.Message is not null)
            SetSuccess(result.Message);

        return RedirectToAction(nameof(AllGroups));
    }

    // --- Resource Sync Dashboard ---

    [HttpGet("Sync")]
    [Authorize(Policy = PolicyNames.TeamsAdminBoardOrAdmin)]
    public IActionResult Sync()
    {
        var viewModel = new TeamSyncViewModel
        {
            CanExecuteActions = RoleChecks.IsAdmin(User)
        };
        return View(viewModel);
    }

    [HttpGet("Sync/Preview/{resourceType}")]
    [Authorize(Policy = PolicyNames.TeamsAdminBoardOrAdmin)]
    public async Task<IActionResult> SyncPreview(GoogleResourceType resourceType)
    {
        var result = resourceType == GoogleResourceType.Group
            ? await googleGroupSync.ReconcileAllAsync(SyncAction.Preview, HttpContext.RequestAborted)
            : await googleSyncService.SyncResourcesByTypeAsync(resourceType, SyncAction.Preview, HttpContext.RequestAborted);

        result.Diffs.Sort((a, b) =>
            string.Compare(a.ResourceName, b.ResourceName, StringComparison.Ordinal));

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
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncExecute(Guid resourceId)
    {
        try
        {
            var result = await googleSyncService.SyncSingleResourceAsync(
                resourceId,
                SyncAction.Execute,
                HttpContext.RequestAborted);
            return Json(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute sync for resource {ResourceId}", resourceId);
            return Json(new { ErrorMessage = ex.Message });
        }
    }

    [HttpPost("Sync/ExecuteAll/{resourceType}")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncExecuteAll(GoogleResourceType resourceType)
    {
        try
        {
            var result = resourceType == GoogleResourceType.Group
                ? await googleGroupSync.ReconcileAllAsync(SyncAction.Execute, HttpContext.RequestAborted)
                : await googleSyncService.SyncResourcesByTypeAsync(resourceType, SyncAction.Execute, HttpContext.RequestAborted);
            return Json(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute sync for resource type {ResourceType}", resourceType);
            return Json(new { ErrorMessage = ex.Message });
        }
    }

    // --- Human Email Provisioning ---

    [HttpPost("Human/{id:guid}/ProvisionEmail")]
    [Authorize(Policy = PolicyNames.HumanAdminOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ProvisionEmail(Guid id, string emailPrefix)
    {
        if (string.IsNullOrWhiteSpace(emailPrefix))
        {
            SetError("Email prefix is required.");
            return RedirectToAction(nameof(ProfileController.AdminDetail), "Profile", new { id });
        }

        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return NotFound();

        var result = await emailProvisioningService.ProvisionNobodiesEmailAsync(
            id, emailPrefix, currentUser.Id);

        if (!result.Success)
        {
            SetError(result.ErrorMessage ?? "Provisioning failed.");
        }
        else
        {
            if (result.RecoveryEmail is not null)
            {
                SetSuccess($"Account {result.FullEmail} provisioned and linked. Credentials sent to {result.RecoveryEmail}.");
            }
            else
            {
                SetSuccess($"Account {result.FullEmail} provisioned and linked. No recovery email found — credentials not sent.");
            }
        }

        return RedirectToAction(nameof(ProfileController.AdminDetail), "Profile", new { id });
    }

    // --- Workspace Accounts ---

    [HttpGet("Accounts")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Accounts()
    {
        var result = await googleAdminService.GetWorkspaceAccountListAsync();

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
                IsUsedAsPrimary = a.IsUsedAsPrimary,
                IsEnrolledIn2Sv = a.IsEnrolledIn2Sv,
                RecoveryEmail = a.RecoveryEmail
            }).ToList(),
            TotalAccounts = result.TotalAccounts,
            ActiveAccounts = result.ActiveAccounts,
            SuspendedAccounts = result.SuspendedAccounts,
            LinkedAccounts = result.LinkedAccounts,
            UnlinkedAccounts = result.UnlinkedAccounts,
            NotPrimaryCount = result.NotPrimaryCount,
            MissingTwoFactorCount = result.MissingTwoFactorCount
        };

        // If a previous POST just issued recovery credentials (password reset
        // ± a 2FA backup code), surface them once via a modal. TempData is
        // single-use — once the page renders, the secrets are gone.
        if (TempData[TempDataKeys.WorkspaceRecoveryCredentials] is string credsJson)
        {
            var credsVm = System.Text.Json.JsonSerializer.Deserialize<WorkspaceRecoveryCredentialsViewModel>(credsJson);
            if (credsVm is not null)
            {
                ViewBag.RecoveryCredentials = credsVm;
            }
        }

        return View(model);
    }

    [HttpPost("Accounts/Provision")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ProvisionAccount(ProvisionWorkspaceAccountModel model)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null) return Unauthorized();

        var result = await googleAdminService.ProvisionStandaloneAccountAsync(
            model.EmailPrefix, model.FirstName, model.LastName,
            currentUser.Id);

        if (result.Success)
            SetSuccess(result.Message!);
        else
            SetError(result.ErrorMessage!);

        return RedirectToAction(nameof(Accounts));
    }

    [HttpPost("Accounts/Suspend")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SuspendAccount(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest();
        }

        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null) return Unauthorized();

        var result = await googleAdminService.SuspendAccountAsync(
            email, currentUser.Id);

        if (result.Success)
            SetSuccess(result.Message!);
        else
            SetError(result.ErrorMessage!);

        return RedirectToAction(nameof(Accounts));
    }

    [HttpPost("Accounts/Reactivate")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReactivateAccount(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest();
        }

        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null) return Unauthorized();

        var result = await googleAdminService.ReactivateAccountAsync(
            email, currentUser.Id);

        if (result.Success)
            SetSuccess(result.Message!);
        else
            SetError(result.ErrorMessage!);

        return RedirectToAction(nameof(Accounts));
    }

    [HttpPost("Accounts/ResetPassword")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(string email)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null) return Unauthorized();

        var result = await googleAdminService.ResetPasswordAsync(
            email, currentUser.Id);

        if (result.Success)
        {
            CarryRecoveryCredentials(new WorkspaceRecoveryCredentialsViewModel
            {
                Email = email,
                TempPassword = result.TemporaryPassword!,
                BackupCode = null
            });
            SetSuccess($"Password reset for {email}. Deliver the new password securely.");
        }
        else
        {
            SetError(result.ErrorMessage ?? $"Failed to reset password for {email}.");
        }

        return RedirectToAction(nameof(Accounts));
    }

    [HttpPost("Accounts/ResetPasswordAndGenerate2Fa")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPasswordAndGenerate2Fa(string email)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null) return Unauthorized();

        var result = await googleAdminService.ResetPasswordAndGenerate2FaAsync(
            email, currentUser.Id);

        if (result.Success && !string.IsNullOrEmpty(result.TempPassword))
        {
            CarryRecoveryCredentials(new WorkspaceRecoveryCredentialsViewModel
            {
                Email = result.Email ?? email,
                TempPassword = result.TempPassword,
                BackupCode = result.BackupCode
            });
            if (!string.IsNullOrEmpty(result.BackupCode))
                SetSuccess(result.Message ?? $"Recovery credentials issued for {email}. Deliver them securely.");
            else
                SetError(result.Message ?? $"Password reset for {email}, but backup-code generation failed. Deliver the password and retry 2FA.");
        }
        else
        {
            SetError(result.ErrorMessage ?? $"Failed to issue recovery credentials for {email}.");
        }

        return RedirectToAction(nameof(Accounts));
    }

    private void CarryRecoveryCredentials(WorkspaceRecoveryCredentialsViewModel vm)
    {
        // Single-use across the PRG redirect — a refresh of /Google/Accounts
        // after dismissing the modal cannot re-expose the secrets.
        TempData[TempDataKeys.WorkspaceRecoveryCredentials] =
            System.Text.Json.JsonSerializer.Serialize(vm);
    }

    [HttpPost("Accounts/Link")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LinkAccount(string email, Guid userId)
    {
        if (string.IsNullOrWhiteSpace(email) || userId == Guid.Empty)
        {
            SetError("Email and human are required.");
            return RedirectToAction(nameof(Accounts));
        }

        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null) return Unauthorized();

        var result = await googleAdminService.LinkAccountAsync(
            email, userId, currentUser.Id);

        if (result.Success)
        {
            SetSuccess(result.Message!);
        }
        else
        {
            SetError(result.ErrorMessage!);
        }

        return RedirectToAction(nameof(Accounts));
    }

    // --- Sync Outbox ---

    [HttpGet("SyncOutbox")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> SyncOutbox(
        [FromServices] IUserServiceRead userService,
        [FromServices] ITeamServiceRead teamService)
    {
        var events = (await googleSyncService.GetRecentOutboxEventsAsync(200)).ToList();

        // Display info via UserInfo cache (one lookup/user). GoogleEmail from IsGoogle row, else primary. BurnerName per burnername-is-the-display-name.
        var userIds = events.Select(e => e.UserId).Distinct().ToList();
        var teamIds = events.Select(e => e.TeamId).Distinct().ToList();
        var googleEmailLookup = new Dictionary<Guid, string>(userIds.Count);
        var displayNameLookup = new Dictionary<Guid, string>(userIds.Count);
        foreach (var userId in userIds)
        {
            var info = await userService.GetUserInfoAsync(userId);
            googleEmailLookup[userId] = info?.GoogleEmail ?? info?.Email ?? "unknown";
            displayNameLookup[userId] = info?.BurnerName ?? "(unknown)";
        }
        var teamsById = await teamService.GetTeamsAsync();
        var teamLookup = teamIds
            .Where(teamsById.ContainsKey)
            .ToDictionary(id => id, id => teamsById[id].Name);
        var resourcesByTeam = await teamResourceService.GetResourcesByTeamIdsAsync(teamIds);
        var resourceLookup = resourcesByTeam.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Select(r => $"{r.Name} ({r.ResourceType})").ToList());

        ViewBag.GoogleEmailLookup = googleEmailLookup;
        ViewBag.DisplayNameLookup = displayNameLookup;
        ViewBag.TeamLookup = teamLookup;
        ViewBag.ResourceLookup = resourceLookup;
        return View(events);
    }

    // --- Email Rename Detection ---

    [HttpPost("CheckEmailRenames")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckEmailRenames()
    {
        try
        {
            var result = await googleAdminService.DetectEmailRenamesAsync();
            TempData[TempDataKeys.EmailRenameResult] = System.Text.Json.JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to check email renames");
            SetError($"Email rename check failed: {ex.Message}");
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction(nameof(EmailRenames));
    }

    [HttpGet("EmailRenames")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public IActionResult EmailRenames()
    {
        EmailRenameDetectionResult? result = null;
        if (TempData[TempDataKeys.EmailRenameResult] is string json)
        {
            result = System.Text.Json.JsonSerializer.Deserialize<EmailRenameDetectionResult>(json);
        }

        if (result is null)
        {
            SetInfo("No email rename results to display. Run the check first.");
            return RedirectToAction(nameof(Index));
        }

        return View(result);
    }

    // --- Email Flag Violations (admin remediation) ---

    [HttpGet("EmailFlagViolations")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> EmailFlagViolations(
        [FromServices] IUserEmailService userEmailService,
        CancellationToken ct)
    {
        var violations = await userEmailService.GetEmailFlagViolationsAsync(ct);
        return View(violations);
    }

    // --- Index ---

    [HttpGet("")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
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
