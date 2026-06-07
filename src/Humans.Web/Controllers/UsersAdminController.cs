// @e2e: board.spec.ts
// @e2e: profile.spec.ts
using Humans.Application;
using Humans.Application.Authorization;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Admin;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.HumanLifecycle;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
[Route("Users/Admin")]
public sealed class UsersAdminController(
    IUserService userService,
    IUserEmailService userEmailService,
    IEmailOutboxServiceRead emailOutboxService,
    IRoleAssignmentService roleAssignmentService,
    IApplicationDecisionService applicationDecisionService,
    IConsentServiceRead consentService,
    ICampaignService campaignService,
    IHumanLifecycleService humanLifecycleService,
    IOnboardingService onboardingService,
    IAuditLogService auditLogService,
    IAdminDatabaseDiagnosticsService databaseDiagnostics,
    IAccountDeletionService accountDeletionService,
    IWebHostEnvironment environment,
    IClock clock,
    IAuthorizationService authorizationService,
    ILogger<UsersAdminController> logger,
    IStringLocalizer<SharedResource> localizer) : HumansControllerBase(userService)
{
    private readonly IUserService _userService = userService;

    [HttpGet("")]
    public async Task<IActionResult> AdminList(
        string? search,
        string? filter,
        string sort = "name",
        string dir = "asc",
        int page = 1,
        CancellationToken ct = default)
    {
        var allRows = await BuildAdminHumansAsync(search, filter, ct);
        var viewModel = AdminHumanListViewModelBuilder.Build(
            allRows,
            search,
            filter,
            sort,
            dir,
            page,
            id => Url.Action(nameof(AdminDetail), "UsersAdmin", new { id }));

        return View(viewModel);
    }

    [HttpGet("Roles")]
    public async Task<IActionResult> Roles(string? role, bool showInactive = false, int page = 1)
    {
        var pageSize = 50;
        var now = clock.GetCurrentInstant();

        var (assignments, totalCount) = await roleAssignmentService.GetFilteredAsync(
            role, activeOnly: !showInactive, page, pageSize, now);

        var viewModel = new AdminRoleAssignmentListViewModel
        {
            RoleAssignments = assignments.Select(ra => new AdminRoleAssignmentViewModel
            {
                Id = ra.Id,
                UserId = ra.UserId,
                UserEmail = ra.UserEmail ?? string.Empty,
                RoleName = ra.RoleName,
                ValidFrom = ra.ValidFrom.ToDateTimeUtc(),
                ValidTo = ra.ValidTo?.ToDateTimeUtc(),
                Notes = ra.Notes,
                IsActive = ra.IsActive(now),
                CreatedByName = ra.CreatedByDisplayName,
                CreatedAt = ra.CreatedAt.ToDateTimeUtc()
            }).ToList(),
            RoleFilter = role,
            ShowInactive = showInactive,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View("~/Views/Shared/Roles.cshtml", viewModel);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> AdminDetail(Guid id, CancellationToken ct)
    {
        var info = await _userService.GetUserInfoAsync(id, ct);
        if (info is null)
            return NotFound();

        var applications = await applicationDecisionService.GetUserApplicationsAsync(id, ct);
        var userEmails = await userEmailService.GetEntitiesByUserIdAsync(id, ct);
        var consentCount = await consentService.GetConsentRecordCountAsync(id, ct);
        var roleAssignments = await roleAssignmentService.GetByUserIdAsync(id, ct);
        var roleCreatorNamesByUserId = (await _userService.GetUserInfosAsync(
                roleAssignments.Select(ra => ra.CreatedByUserId).Distinct().ToList(), ct))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.BurnerName);
        var campaignGrants = await campaignService.GetAllGrantsForUserAsync(id, ct);
        var outboxCount = await emailOutboxService.GetMessageCountForUserAsync(id, ct);
        var revealedIban = TempData.TryGetValue("RevealedIban", out var revealed) && revealed is string value
            ? value
            : null;

        // If this is a merge tombstone, resolve the survivor's name for the banner.
        var mergedToName = info.MergedToUserId is Guid mergedTo
            ? (await _userService.GetUserInfoAsync(mergedTo, ct))?.BurnerName
            : null;

        var viewModel = AdminHumanDetailViewModelBuilder.Build(
            info,
            applications,
            userEmails,
            consentCount,
            roleAssignments,
            roleCreatorNamesByUserId,
            campaignGrants,
            outboxCount,
            clock.GetCurrentInstant(),
            await GetRejectedByNameAsync(info.Profile, ct),
            revealedIban,
            mergedToName);

        return View(viewModel);
    }

    [Authorize(Policy = PolicyNames.AdminOnly)]
    [HttpPost("{id:guid}/RevealIban")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevealIban(Guid id, CancellationToken ct)
    {
        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null) return Forbid();
        return await RevealIbanCoreAsync(id, actor.Id, ct);
    }

    [HttpGet("{id:guid}/Outbox")]
    public async Task<IActionResult> AdminOutbox(Guid id, CancellationToken ct)
    {
        var messages = await emailOutboxService.GetMessagesForUserAsync(id, ct);

        ViewBag.HumanId = id;
        return View("~/Views/Profile/Outbox.cshtml", messages);
    }

    [HttpPost("{id:guid}/Suspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SuspendHuman(Guid id, string? notes)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return NotFound();

        var result = await humanLifecycleService.SuspendAsync(id, currentUser.Id, notes);
        if (!result.Success)
            return NotFound();

        SetSuccess(localizer["Admin_MemberSuspended"].Value);
        return RedirectToAction(nameof(AdminDetail), new { id });
    }

    [HttpPost("{id:guid}/Unsuspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnsuspendHuman(Guid id)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return NotFound();

        var result = await humanLifecycleService.UnsuspendAsync(id, currentUser.Id);
        if (!result.Success)
            return NotFound();

        SetSuccess(localizer["Admin_MemberUnsuspended"].Value);
        return RedirectToAction(nameof(AdminDetail), new { id });
    }

    [HttpPost("{id:guid}/Reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectSignup(Guid id, string? reason)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return Unauthorized();

        var result = await onboardingService.RejectSignupAsync(id, currentUser.Id, reason);
        if (!result.Success)
        {
            if (string.Equals(result.ErrorKey, "AlreadyRejected", StringComparison.Ordinal))
                SetError("This human has already been rejected.");
            else
                return NotFound();
            return RedirectToAction(nameof(AdminDetail), new { id });
        }

        SetSuccess("Signup rejected.");
        return RedirectToAction(nameof(AdminDetail), new { id });
    }

    [HttpGet("{id:guid}/Roles/Add")]
    public async Task<IActionResult> AddRole(Guid id)
    {
        var info = await _userService.GetUserInfoAsync(id);
        if (info is null)
        {
            return NotFound();
        }

        var viewModel = new CreateRoleAssignmentViewModel
        {
            UserId = id,
            AvailableRoles = [.. RoleChecks.GetAssignableRoles(User)]
        };

        return View(viewModel);
    }

    [HttpPost("{id:guid}/Roles/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddRole(Guid id, CreateRoleAssignmentViewModel model)
    {
        var info = await _userService.GetUserInfoAsync(id);
        if (info is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(model.RoleName))
        {
            ModelState.AddModelError(nameof(model.RoleName), "Please select a role.");
            PopulateRoleAssignmentForm(model, id);
            return View(model);
        }

        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
        {
            return Unauthorized();
        }

        var authResult = await authorizationService.AuthorizeAsync(
            User, model.RoleName, RoleAssignmentOperationRequirement.Manage);
        if (!authResult.Succeeded)
        {
            logger.LogWarning(
                "Authorization denied for role assignment: principal {Principal} attempted to assign role {Role} to user {UserId}",
                User.Identity?.Name, model.RoleName, id);
            return Forbid();
        }

        var result = await roleAssignmentService.AssignRoleAsync(
            id, model.RoleName, currentUser.Id, model.Notes);

        SetRoleAssignmentResult(model.RoleName, result.Success);
        return RedirectToAction(nameof(AdminDetail), new { id });
    }

    [HttpPost("{id:guid}/Roles/{roleId:guid}/End")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EndRole(Guid id, Guid roleId, string? notes)
    {
        var roleAssignment = await roleAssignmentService.GetByIdAsync(roleId);

        if (roleAssignment is null)
        {
            return NotFound();
        }

        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
        {
            return Unauthorized();
        }

        var authResult = await authorizationService.AuthorizeAsync(
            User, roleAssignment.RoleName, RoleAssignmentOperationRequirement.Manage);
        if (!authResult.Succeeded)
        {
            logger.LogWarning(
                "Authorization denied for ending role: principal {Principal} attempted to end role {Role} for user {UserId}",
                User.Identity?.Name, roleAssignment.RoleName, roleAssignment.UserId);
            return NotFound();
        }

        var result = await roleAssignmentService.EndRoleAsync(
            roleId, currentUser.Id, notes);

        SetRoleEndedResult(result.Success, roleAssignment.RoleName, roleAssignment.UserDisplayName);
        return RedirectToAction(nameof(AdminDetail), new { id = roleAssignment.UserId });
    }

    // Audience-segmentation diagnostic — relocated here from AdminController by the
    // "legacy admin routes -> section homes" move (#901). Admin-only (stricter than the
    // class-level Board-or-Admin gate, mirroring RevealIban).
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [HttpGet("Audience")]
    public async Task<IActionResult> Audience(int? year, CancellationToken ct)
    {
        try
        {
            var segmentation = await databaseDiagnostics.GetAudienceSegmentationAsync(year, ct);

            var model = new AudienceSegmentationViewModel
            {
                TotalAccounts = segmentation.TotalAccounts,
                WithTicket = segmentation.WithTicket,
                WithProfile = segmentation.WithProfile,
                WithBoth = segmentation.WithBoth,
                WithNeither = segmentation.WithNeither,
                AvailableYears = segmentation.AvailableYears.ToList(),
                SelectedYear = segmentation.SelectedYear,
            };

            return View(model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading audience segmentation data");
            SetError("Failed to load audience segmentation data.");
            return RedirectToAction(nameof(AdminController.Index), "Admin");
        }
    }

    // Dev/QA-only destructive purge — moved here with the rest of the per-user admin surface.
    [HttpPost("{id:guid}/Purge")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PurgeHuman(Guid id)
    {
        if (environment.IsProduction())
            return NotFound();

        var user = await _userService.GetUserInfoAsync(id);
        if (user is null)
            return NotFound();

        var currentUser = await GetCurrentUserInfoAsync();
        if (user.Id == currentUser?.Id)
        {
            SetError("You cannot purge your own account.");
            return RedirectToAction(nameof(AdminDetail), new { id });
        }

        var displayName = user.BurnerName;
        logger.LogWarning(
            "Admin {AdminId} purging human {HumanId} ({DisplayName}) in {Environment}",
            currentUser?.Id, id, displayName, environment.EnvironmentName);

        var result = await accountDeletionService.PurgeAsync(id, currentUser?.Id);
        if (!result.Success)
            return NotFound();

        SetSuccess($"Purged {displayName}. They will get a fresh account on next login.");
        return RedirectToAction(nameof(AdminList));
    }

    private async Task<IReadOnlyList<AdminHumanRow>> BuildAdminHumansAsync(
        string? search, string? statusFilter, CancellationToken ct)
    {
        var allUsers = await _userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);
        var allUserIds = allUsers.Select(u => u.Id).ToList();
        var notificationEmails =
            await userEmailService.GetNotificationEmailsByUserIdsAsync(allUserIds, ct);

        IReadOnlySet<Guid>? searchUserIds = null;
        if (!string.IsNullOrWhiteSpace(search))
        {
            // Uncapped: used as a match set to filter the admin list (ordering is the table's own),
            // so a hard cap would silently drop matching humans. Cheap at ~500 users.
            var searchResults = await _userService.SearchUsersAsync(
                search, PersonSearchFields.AdminAll, limit: int.MaxValue, ct);

            var byEmail = allUsers
                .Where(u =>
                    (u.Email ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    u.BurnerName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Select(u => u.Id);

            searchUserIds = searchResults
                .Select(r => r.UserId)
                .Concat(byEmail)
                .ToHashSet();
        }

        return AdminHumanListAssembler.Assemble(
            allUsers,
            notificationEmails,
            searchUserIds,
            statusFilter);
    }

    private async Task<string?> GetRejectedByNameAsync(ProfileInfo? profile, CancellationToken ct)
    {
        if (profile?.RejectedByUserId is null)
            return null;

        var rejectedByInfo = await _userService.GetUserInfoAsync(profile.RejectedByUserId.Value, ct);
        return rejectedByInfo?.BurnerName;
    }

    private async Task<IActionResult> RevealIbanCoreAsync(Guid id, Guid actorId, CancellationToken ct)
    {
        var info = await _userService.GetUserInfoAsync(id, ct);
        var iban = info?.Profile?.Iban;
        if (iban is null)
        {
            SetError("No IBAN on record for this user.");
            return RedirectToAction(nameof(AdminDetail), new { id });
        }
        await auditLogService.LogAsync(
            AuditAction.IbanReveal, nameof(User), id,
            $"Admin revealed IBAN for user {id}", actorId);
        TempData["RevealedIban"] = iban;
        return RedirectToAction(nameof(AdminDetail), new { id });
    }

    private void PopulateRoleAssignmentForm(CreateRoleAssignmentViewModel model, Guid userId)
    {
        model.UserId = userId;
        model.AvailableRoles = [.. RoleChecks.GetAssignableRoles(User)];
    }

    private void SetRoleAssignmentResult(string roleName, bool success)
    {
        if (success)
        {
            SetSuccess(string.Format(localizer["Admin_RoleAssigned"].Value, roleName));
            return;
        }

        SetError(string.Format(localizer["Admin_RoleAlreadyActive"].Value, roleName));
    }

    private void SetRoleEndedResult(bool success, string roleName, string userDisplayName)
    {
        if (success)
        {
            SetSuccess(string.Format(localizer["Admin_RoleEnded"].Value, roleName, userDisplayName));
            return;
        }

        SetError(localizer["Admin_RoleNotActive"].Value);
    }
}
