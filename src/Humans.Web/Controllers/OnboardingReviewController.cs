using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Onboarding;

namespace Humans.Web.Controllers;

/// <summary>
/// Review queue for Consent Coordinators and Volunteer Coordinators.
/// Manages the consent check gate for new humans during onboarding.
/// </summary>
[Authorize(Policy = PolicyNames.ReviewQueueAccess)]
[Route("[controller]")]
public class OnboardingReviewController(
    IUserService userService,
    IOnboardingService onboardingService,
    ILogger<OnboardingReviewController> logger,
    IStringLocalizer<SharedResource> localizer) : HumansControllerBase(userService)
{
    private readonly IUserService _userService = userService;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var data = await onboardingService.GetReviewQueueAsync(ct);

        var viewModel = new OnboardingReviewIndexViewModel
        {
            PendingReviews = data.Pending.Select(u => MapToItem(u, data.PendingAppUserIds, data.ConsentProgress)).ToList(),
            FlaggedReviews = data.Flagged.Select(u => MapToItem(u, data.PendingAppUserIds, data.ConsentProgress)).ToList()
        };

        return View(viewModel);
    }

    [HttpGet("{userId:guid}")]
    public async Task<IActionResult> Detail(Guid userId, CancellationToken ct)
    {
        var detail = await onboardingService.GetReviewDetailAsync(userId, ct);
        var (profile, consentCount, requiredConsentCount, pendingApplicationMotivation) =
            (detail.Profile, detail.ConsentCount, detail.RequiredConsentCount, detail.PendingApplicationMotivation);

        if (profile is null)
            return NotFound();

        var detailUser = await _userService.GetUserInfoAsync(userId, ct);

        var viewModel = new OnboardingReviewDetailViewModel
        {
            UserId = userId,
            Email = detailUser?.Email ?? string.Empty,
            FirstName = profile.FirstName,
            LastName = profile.LastName,
            City = profile.City,
            CountryCode = profile.CountryCode,
            MembershipTier = profile.MembershipTier,
            ConsentCheckStatus = profile.ConsentCheckStatus,
            ConsentCheckNotes = profile.ConsentCheckNotes,
            ProfileCreatedAt = profile.CreatedAt.ToDateTimeUtc(),
            ConsentCount = consentCount,
            RequiredConsentCount = requiredConsentCount,
            HasPendingApplication = pendingApplicationMotivation is not null,
            ApplicationMotivation = pendingApplicationMotivation
        };

        return View(viewModel);
    }

    [HttpPost("{userId:guid}/Clear")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.ConsentCoordinatorBoardOrAdmin)]
    public async Task<IActionResult> Clear(Guid userId, string? notes)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return NotFound();

        try
        {
            var result = await onboardingService.ClearConsentCheckAsync(
                userId, currentUser.Id, notes);

            if (!result.Success)
            {
                SetError(result.ErrorKey switch
                {
                    "AlreadyRejected" => localizer["OnboardingReview_AlreadyRejected"].Value,
                    _ => localizer["Common_Error"].Value
                });
                return RedirectToAction(nameof(Index));
            }

            SetSuccess(localizer["OnboardingReview_Cleared"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clear consent check for user {UserId}", userId);
            SetError(localizer["Common_Error"].Value);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("BulkClear")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.ConsentCoordinatorBoardOrAdmin)]
    public async Task<IActionResult> BulkClear([FromForm] List<Guid> selectedUserIds, CancellationToken ct)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return NotFound();

        try
        {
            var result = await onboardingService.BulkClearConsentChecksAsync(
                selectedUserIds, currentUser.Id, ct);
            SetBulkClearResultMessage(result, selectedUserIds.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to bulk clear consent checks for {Count} users: {UserIds}",
                selectedUserIds.Count,
                selectedUserIds);
            SetError(localizer["Common_Error"].Value);
        }

        return RedirectToAction(nameof(Index));
    }

    private void SetBulkClearResultMessage(BulkOnboardingResult result, int selectedCount)
    {
        if (result.ApprovedCount == 0)
        {
            SetInfo(localizer["OnboardingReview_BulkClearedNone"].Value);
        }
        else if (result.ApprovedCount < selectedCount)
        {
            SetSuccess(localizer["OnboardingReview_BulkClearedPartial", result.ApprovedCount, selectedCount].Value);
        }
        else
        {
            SetSuccess(localizer["OnboardingReview_BulkCleared", result.ApprovedCount].Value);
        }
    }

    [HttpPost("{userId:guid}/Flag")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.ConsentCoordinatorBoardOrAdmin)]
    public async Task<IActionResult> Flag(Guid userId, string? notes)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return NotFound();

        try
        {
            var result = await onboardingService.FlagConsentCheckAsync(
                userId, currentUser.Id, notes);

            if (!result.Success)
            {
                SetError(localizer["Common_Error"].Value);
                return RedirectToAction(nameof(Index));
            }

            SetSuccess(localizer["OnboardingReview_Flagged"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to flag consent check for user {UserId}", userId);
            SetError(localizer["Common_Error"].Value);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{userId:guid}/Reject")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.ConsentCoordinatorBoardOrAdmin)]
    public async Task<IActionResult> Reject(Guid userId, string? reason)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return NotFound();

        try
        {
            var result = await onboardingService.RejectSignupAsync(
                userId, currentUser.Id, reason);

            SetRejectSignupResultMessage(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reject signup for user {UserId}", userId);
            SetError(localizer["Common_Error"].Value);
        }
        return RedirectToAction(nameof(Index));
    }

    private void SetRejectSignupResultMessage(OnboardingResult result)
    {
        if (result.Success)
        {
            SetSuccess(localizer["OnboardingReview_Rejected"].Value);
            return;
        }

        SetError(string.Equals(result.ErrorKey, "AlreadyRejected", StringComparison.Ordinal)
            ? localizer["OnboardingReview_AlreadyRejected"].Value
            : localizer["Common_Error"].Value);
    }

    private static OnboardingReviewItemViewModel MapToItem(
        UserInfo info,
        HashSet<Guid> pendingAppUserIds,
        Dictionary<Guid, ConsentProgressInfo> consentProgress)
    {
        var progress = consentProgress.GetValueOrDefault(info.Id);
        var profile = info.Profile!;
        return new OnboardingReviewItemViewModel
        {
            UserId = info.Id,
            LegalName = profile.FullName,
            Email = info.Email ?? string.Empty,
            ConsentCheckStatus = profile.ConsentCheckStatus,
            MembershipTier = profile.MembershipTier,
            ProfileCreatedAt = profile.CreatedAt.ToDateTimeUtc(),
            HasPendingApplication = pendingAppUserIds.Contains(info.Id),
            ConsentCount = progress?.Signed ?? 0,
            RequiredConsentCount = progress?.Required ?? 0
        };
    }
}
