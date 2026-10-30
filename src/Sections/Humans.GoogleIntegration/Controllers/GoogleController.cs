using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.GoogleIntegration.Models;
using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.GoogleIntegration.Services;

namespace Humans.GoogleIntegration.Controllers;

[Route("Google")]
internal sealed class GoogleController(
    IUserService userService,
    IGoogleSyncManagement googleSyncService,
    IGoogleGroupSync googleGroupSync,
    ITeamResourceManagement teamResourceService,
    IEmailProvisioningService emailProvisioningService,
    IGoogleAdminService googleAdminService,
    IGoogleIntegrationService workspaceIdentities,
    IGoogleSyncLogService syncLog,
    ILogger<GoogleController> logger) : HumansControllerBase(userService)
{
    [HttpGet("Resource/{id:guid}")]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    public async Task<IActionResult> Resource(Guid id)
    {
        var resource = await teamResourceService.GetResourceByIdAsync(id, HttpContext.RequestAborted);
        if (resource is null)
            return NotFound();

        return View("SyncAudit", new SyncAuditViewModel(
            $"Sync Audit: {resource.Name}",
            Url.Action(nameof(Sync)),
            "Back to Sync Status",
            ResourceId: id,
            UserId: null));
    }

    [HttpGet("Human/{id:guid}")]
    [Authorize(Policy = UsersPolicies.Read)]
    public async Task<IActionResult> Human(Guid id)
    {
        var user = await FindUserInfoByIdAsync(id, HttpContext.RequestAborted);
        if (user is null)
            return NotFound();

        return View("SyncAudit", new SyncAuditViewModel(
            $"Google Sync Audit: {user.BurnerName}",
            Url.Action("AdminDetail", "UsersAdmin", new { id }),
            "Back to Human Detail",
            ResourceId: null,
            UserId: id));
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
        if (!ModelState.IsValid) return BadRequest(ModelState);

        await syncSettingsService.UpdateModeAsync(serviceType, mode, currentUser.Id);

        logger.LogInformation("Admin {AdminId} changed {ServiceType} sync mode to {Mode}",
            currentUser.Id, serviceType, mode);

        SetSuccess($"Sync mode for {SyncServiceNameFormatter.Format(serviceType)} updated to {mode}.");
        return Redirect("/Settings#google-sync");
    }

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

    [HttpGet("ActivityLog")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> ActivityLog(
        NodaTime.LocalDate? from, NodaTime.LocalDate? to, GoogleSyncLogAction? action,
        GoogleSyncSource? source, bool? success, string? resource)
    {
        var entries = await syncLog.GetRecentAsync(
            from?.AtStartOfDayInZone(NodaTime.DateTimeZone.Utc).ToInstant(),
            to?.PlusDays(1).AtStartOfDayInZone(NodaTime.DateTimeZone.Utc).ToInstant(),
            action, source, success, resource, HttpContext.RequestAborted);

        return View(new ActivityLogViewModel(from, to, action, source, success, resource, entries));
    }

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

    [HttpGet("AllGroups")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> AllGroups()
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            var result = await googleSyncService.GetAllDomainGroupsAsync(ct);
            var teams = (await googleAdminService.GetActiveTeamsAsync(ct))
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList();
            ViewBag.Teams = teams;
            return View(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning("Domain group listing cancelled by caller");
            throw;
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

    [HttpGet("Sync")]
    [Authorize(Policy = TeamsPolicies.Read)]
    public IActionResult Sync()
    {
        return View();
    }

    // POST (HUM0038): a preview writes the resource's error column when a Google lookup fails.
    // CancellationToken.None, as on the execute paths.
    [HttpPost("Sync/Preview/{resourceType}")]
    [Authorize(Policy = TeamsPolicies.Read)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncPreview(GoogleResourceType resourceType)
    {
        var result = resourceType == GoogleResourceType.Group
            ? await googleGroupSync.ReconcileAllAsync(SyncAction.Preview, CancellationToken.None, GoogleSyncSource.ManualSync)
            : await googleSyncService.SyncResourcesByTypeAsync(resourceType, SyncAction.Preview, CancellationToken.None);

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
            ResourceType = resourceType.ToString()
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
            // Not RequestAborted: closing the tab mid-reconcile would leave Workspace half-synced,
            // with no record of where it stopped.
            var result = await googleSyncService.SyncSingleResourceAsync(
                resourceId,
                SyncAction.Execute,
                CancellationToken.None);
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
            // Not RequestAborted: see SyncExecute.
            var result = resourceType == GoogleResourceType.Group
                ? await googleGroupSync.ReconcileAllAsync(SyncAction.Execute, CancellationToken.None, GoogleSyncSource.ManualSync)
                : await googleSyncService.SyncResourcesByTypeAsync(resourceType, SyncAction.Execute, CancellationToken.None);
            return Json(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute sync for resource type {ResourceType}", resourceType);
            return Json(new { ErrorMessage = ex.Message });
        }
    }

    [HttpPost("Human/{id:guid}/ProvisionEmail")]
    [Authorize(Policy = UsersPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ProvisionEmail(Guid id, string emailPrefix)
    {
        if (string.IsNullOrWhiteSpace(emailPrefix))
        {
            SetError("Email prefix is required.");
            return RedirectToAction("AdminDetail", "UsersAdmin", new { id });
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
            if (result.WarningMessage is not null)
            {
                SetInfo(result.WarningMessage);
            }
            else
            {
                SetSuccess($"Account {result.FullEmail} provisioned and linked. Credentials email queued for {result.RecoveryEmail}.");
            }
        }

        return RedirectToAction("AdminDetail", "UsersAdmin", new { id });
    }

    [HttpGet("Accounts")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Accounts()
    {
        var result = await googleAdminService.GetWorkspaceAccountListAsync(HttpContext.RequestAborted);

        if (result.ErrorMessage is not null)
        {
            SetError(result.ErrorMessage);
        }

        var model = new WorkspaceEmailListViewModel
        {
            Accounts = result.Accounts,
            TotalAccounts = result.TotalAccounts,
            ActiveAccounts = result.ActiveAccounts,
            SuspendedAccounts = result.SuspendedAccounts,
            LinkedAccounts = result.LinkedAccounts,
            UnlinkedAccounts = result.UnlinkedAccounts,
            NotPrimaryCount = result.NotPrimaryCount,
            MissingTwoFactorCount = result.MissingTwoFactorCount
        };

        // TempData is single-use: the recovery credentials show once, then are gone.
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

    [HttpGet("SyncOutbox")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> SyncOutbox(
        [FromServices] ITeamService teamService,
        [FromServices] IGoogleDriveActivityClient googleClient)
    {
        var ct = HttpContext.RequestAborted;
        ViewData["GoogleNotConfigured"] = !googleClient.IsConfigured;
        var events = (await googleSyncService.GetRecentOutboxEventsAsync(200, ct)).ToList();

        var userIds = events.Select(e => e.UserId).Distinct().ToList();
        var teamIds = events.Select(e => e.TeamId).Distinct().ToList();
        IReadOnlyDictionary<Guid, UserInfo> infos = userIds.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await UserService.GetUserInfosAsync(userIds, ct);
        var identities = await workspaceIdentities.GetWorkspaceIdentitiesAsync(userIds, ct);
        var googleEmailLookup = userIds.ToDictionary(userId => userId,
            userId => identities.GetValueOrDefault(userId)?.Email ?? infos.GetValueOrDefault(userId)?.Email ?? "unknown");
        var displayNameLookup = userIds.ToDictionary(userId => userId,
            userId => infos.GetValueOrDefault(userId)?.BurnerName ?? "(unknown)");
        var teamsById = await teamService.GetTeamsAsync(ct);
        var teamLookup = teamIds
            .Where(teamsById.ContainsKey)
            .ToDictionary(id => id, id => teamsById[id].Name);
        var resourcesByTeam = await teamResourceService.GetResourcesByTeamIdsAsync(teamIds, ct);
        var resourceLookup = resourcesByTeam.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.OrderBy(r => r.ProvisionedAt).Select(r => $"{r.Name} ({r.ResourceType})").ToList());

        ViewBag.GoogleEmailLookup = googleEmailLookup;
        ViewBag.DisplayNameLookup = displayNameLookup;
        ViewBag.TeamLookup = teamLookup;
        ViewBag.ResourceLookup = resourceLookup;
        return View(events);
    }

    [HttpPost("SyncOutbox/{id:guid}/Requeue")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequeueOutboxEvent(Guid id)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null) return Unauthorized();

        var requeued = await googleSyncService.RequeueOutboxEventAsync(id, currentUser.Id);
        if (!requeued)
        {
            SetError("Event not found or not in a failed state.");
            return RedirectToAction(nameof(SyncOutbox));
        }

        SetSuccess("Event requeued for retry.");
        return RedirectToAction(nameof(SyncOutbox));
    }

    [HttpPost("SyncOutbox/RequeueAll")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequeueAllFailedOutboxEvents()
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null) return Unauthorized();

        var count = await googleSyncService.RequeueAllFailedOutboxEventsAsync(currentUser.Id);

        SetSuccess(count == 0
            ? "No permanently-failed events to requeue."
            : $"Requeued {count} event(s) for retry.");
        return RedirectToAction(nameof(SyncOutbox));
    }

    [HttpPost("Human/{id:guid}/RerunSync")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RerunGoogleSync(Guid id)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null) return Unauthorized();

        var count = await googleSyncService.EnqueueUserSyncAsync(id, currentUser.Id);

        SetSuccess(count == 0
            ? "User is not a member of any teams — no sync events enqueued."
            : $"Enqueued sync for {count} team(s). The job will process them shortly.");
        return RedirectToAction("AdminDetail", "UsersAdmin", new { id });
    }

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

    [HttpGet("EmailFlagViolations")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> EmailFlagViolations(
        [FromServices] IUserEmailService userEmailService,
        CancellationToken ct)
    {
        var violations = await userEmailService.GetEmailFlagViolationsAsync(ct);
        return View(violations);
    }

    [HttpGet("")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public IActionResult Index()
    {
        return View();
    }
}
