using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Consent.Models;
using Humans.Consent.Services;
using Humans.Onboarding.Contracts;
using Humans.Base;
using Humans.Base.Authorization;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;

namespace Humans.Consent.Controllers;

[Authorize(Policy = PolicyNames.ReviewQueueAccess)]
[Route("Consent/ReviewQueue")]
internal sealed class ConsentReviewQueueController(
    IUserService userService,
    IConsentReviewQueueService reviewQueue,
    IOnboardingIntake onboarding,
    ILogger<ConsentReviewQueueController> logger,
    IStringLocalizer<ConsentResource> localizer,
    IStringLocalizer<SharedResource> sharedLocalizer) : HumansControllerBase(userService)
{
    private readonly IUserService _userService = userService;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var data = await reviewQueue.GetReviewQueueAsync(ct);

        var viewModel = new ConsentReviewQueueIndexViewModel
        {
            PendingReviews = data.Pending.Select(u => MapToItem(u, data)).ToList(),
            FlaggedReviews = data.Flagged.Select(u => MapToItem(u, data)).ToList()
        };

        return View(viewModel);
    }

    [HttpGet("{userId:guid}")]
    public async Task<IActionResult> Detail(Guid userId, CancellationToken ct)
    {
        var detailUser = await _userService.GetUserInfoAsync(userId, ct);
        if (detailUser is null)
            return NotFound();

        // A merged-away id resolves to the survivor. Land on the survivor's route so the forms post the id
        // the page shows.
        if (detailUser.Id != userId)
            return RedirectToAction(nameof(Detail), new { userId = detailUser.Id });

        var detail = await reviewQueue.GetReviewDetailAsync(userId, ct);
        var (profile, consentCount, requiredConsentCount, pendingApplicationMotivation) =
            (detail.Profile, detail.ConsentCount, detail.RequiredConsentCount, detail.PendingApplicationMotivation);

        if (profile is null)
            return NotFound();

        var viewModel = new ConsentReviewQueueDetailViewModel
        {
            UserId = userId,
            Email = detailUser.Email ?? string.Empty,
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
            var result = await reviewQueue.ClearAsync(
                userId, currentUser.Id, notes);

            if (!result.Success)
            {
                SetError(result.ErrorKey switch
                {
                    "AlreadyRejected" => localizer["Consent_Queue_AlreadyRejected"].Value,
                    _ => sharedLocalizer["Common_Error"].Value
                });
                return RedirectToAction(nameof(Index));
            }

            SetSuccess(localizer["Consent_Queue_Cleared"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clear consent check for user {UserId}", userId);
            SetError(sharedLocalizer["Common_Error"].Value);
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
            var cleared = await reviewQueue.BulkClearAsync(selectedUserIds, currentUser.Id, ct);
            SetBulkClearResultMessage(cleared, selectedUserIds.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to bulk clear consent checks for {Count} users: {UserIds}",
                selectedUserIds.Count,
                selectedUserIds);
            SetError(sharedLocalizer["Common_Error"].Value);
        }

        return RedirectToAction(nameof(Index));
    }

    private void SetBulkClearResultMessage(int cleared, int selectedCount)
    {
        if (cleared == 0)
        {
            SetInfo(localizer["Consent_Queue_BulkClearedNone"].Value);
        }
        else if (cleared < selectedCount)
        {
            SetSuccess(localizer["Consent_Queue_BulkClearedPartial", cleared, selectedCount].Value);
        }
        else
        {
            SetSuccess(localizer["Consent_Queue_BulkCleared", cleared].Value);
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
            var result = await reviewQueue.FlagAsync(
                userId, currentUser.Id, notes);

            if (!result.Success)
            {
                SetError(sharedLocalizer["Common_Error"].Value);
                return RedirectToAction(nameof(Index));
            }

            SetSuccess(localizer["Consent_Queue_Flagged"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to flag consent check for user {UserId}", userId);
            SetError(sharedLocalizer["Common_Error"].Value);
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
            var result = await onboarding.RejectSignupAsync(
                userId, currentUser.Id, reason);

            SetRejectSignupResultMessage(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reject signup for user {UserId}", userId);
            SetError(sharedLocalizer["Common_Error"].Value);
        }
        return RedirectToAction(nameof(Index));
    }

    private void SetRejectSignupResultMessage(OnboardingResult result)
    {
        if (result.Success)
        {
            SetSuccess(localizer["Consent_Queue_Rejected"].Value);
            return;
        }

        SetError(string.Equals(result.ErrorKey, "AlreadyRejected", StringComparison.Ordinal)
            ? localizer["Consent_Queue_AlreadyRejected"].Value
            : sharedLocalizer["Common_Error"].Value);
    }

    private static ConsentReviewQueueItemViewModel MapToItem(UserInfo info, ReviewQueueData data)
    {
        var progress = data.ConsentProgress.GetValueOrDefault(info.Id);
        var profile = info.Profile!;
        return new ConsentReviewQueueItemViewModel
        {
            UserId = info.Id,
            LegalName = profile.FullName,
            Email = info.Email ?? string.Empty,
            ConsentCheckStatus = data.Reviews.GetValueOrDefault(info.Id)?.Status,
            MembershipTier = data.Tiers.GetValueOrDefault(info.Id, MembershipTier.Volunteer),
            ProfileCreatedAt = profile.CreatedAt.ToDateTimeUtc(),
            HasPendingApplication = data.PendingAppUserIds.Contains(info.Id),
            ConsentCount = progress?.Signed ?? 0,
            RequiredConsentCount = progress?.Required ?? 0
        };
    }
}
