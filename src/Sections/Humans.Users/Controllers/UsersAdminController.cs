using Humans.Users.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
// @e2e: board.spec.ts
// @e2e: profile.spec.ts
using Humans.Consent.Contracts;
using Humans.Governance.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Users.Contracts;
using Humans.Base.Controllers;
using Humans.Base;
using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Users.Services;

namespace Humans.Users.Controllers;

[Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
[Route("Users/Admin")]
internal sealed class UsersAdminController(
    IUserManagement userService,
    IUserEmailService userEmailService,
    IGovernanceService applicationDecisionService,
    IConsentService consentService,
    IHumanLifecycleService humanLifecycleService,
    IOnboardingIntake onboardingService,
    IUsersAudienceService audienceService,
    IAccountDeletionService accountDeletionService,
    IWebHostEnvironment environment,
    IClock clock,
    ILogger<UsersAdminController> logger,
    IStringLocalizer<SharedResource> localizer) : HumansControllerBase(userService)
{
    private readonly IUserManagement _userService = userService;

    [HttpGet("")]
    public async Task<IActionResult> AdminList(
        string? search,
        string? filter,
        string sort = "name",
        string dir = "asc",
        int page = 1,
        CancellationToken ct = default)
    {
        search = search?.Trim();
        var allUsers = await _userService.GetAllRawUserInfosAsync(ct).ConfigureAwait(false);
        var allUserIds = allUsers.Select(u => u.Id).ToList();
        var notificationEmails =
            await userEmailService.GetNotificationEmailsByUserIdsAsync(allUserIds, ct);

        IReadOnlySet<Guid>? searchUserIds = null;
        if (!string.IsNullOrWhiteSpace(search))
        {
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

        var allRows = AdminHumanListAssembler.Assemble(
            allUsers,
            notificationEmails,
            searchUserIds,
            filter);

        var viewModel = AdminHumanListViewModelBuilder.Build(
            allRows,
            search,
            filter,
            sort,
            dir,
            page);

        return View(viewModel);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> AdminDetail(Guid id, CancellationToken ct)
    {
        var info = await _userService.GetRawUserInfoAsync(id, ct);
        if (info is null)
            return NotFound();

        var applications = await applicationDecisionService.GetUserApplicationsAsync(id, ct);
        var membershipTier = await applicationDecisionService.GetMembershipTierAsync(id, ct);
        var userEmails = await userEmailService.GetEntitiesByUserIdAsync(id, ct);
        var consentCount = await consentService.GetConsentRecordCountAsync(id, ct);

        var rejectedByName = info.Profile?.RejectedByUserId is Guid rejectedByUserId
            ? (await _userService.GetRawUserInfoAsync(rejectedByUserId, ct))?.BurnerName
            : null;

        var viewModel = AdminHumanDetailViewModelBuilder.Build(
            info,
            applications,
            membershipTier,
            userEmails,
            consentCount,
            clock.GetCurrentInstant(),
            rejectedByName);

        return View(viewModel);
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
            if (!string.Equals(result.ErrorKey, "AlreadyRejected", StringComparison.Ordinal))
                return NotFound();

            SetError("This human has already been rejected.");
            return RedirectToAction(nameof(AdminDetail), new { id });
        }

        SetSuccess("Signup rejected.");
        return RedirectToAction(nameof(AdminDetail), new { id });
    }

    // Audience-segmentation diagnostic — relocated here from AdminController by the
    // "legacy admin routes -> section homes" move (#901). Admin-only (stricter than the
    // class-level Board-or-Admin gate).
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [HttpGet("Audience")]
    public async Task<IActionResult> Audience(int? year, CancellationToken ct)
    {
        try
        {
            var segmentation = await audienceService.GetAudienceSegmentationAsync(year, ct);

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading audience segmentation data");
            SetError("Failed to load audience segmentation data.");
            return RedirectToAction("Index", "Admin");
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

        var user = await _userService.GetRawUserInfoAsync(id);
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

}
