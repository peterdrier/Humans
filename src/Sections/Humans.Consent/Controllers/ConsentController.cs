using Humans.Base.Attributes;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Consent.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Consent.Models;
using Humans.Consent.Services;
using Humans.Users.Contracts;

namespace Humans.Consent.Controllers;

[Authorize]
[AllowBeforeActiveMembership]
internal sealed class ConsentController(
    IUserService userService,
    IConsentManagement consentService,
    IOnboardingIntake onboardingService,
    IStringLocalizer<ConsentResource> localizer,
    ILogger<ConsentController> logger) : HumansControllerBase(userService)
{
    public async Task<IActionResult> Index()
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null)
            return NotFound();

        var (groups, history) = await consentService.GetConsentDashboardAsync(user.Id, HttpContext.RequestAborted);

        var viewModel = new ConsentIndexViewModel
        {
            TeamGroups = groups,
            ConsentHistory = history.Take(10).ToList()
        };

        return View(viewModel);
    }

    [HttpGet]
    public async Task<IActionResult> Review(Guid id)
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null)
            return NotFound();

        // The same name check SubmitConsentAsync refuses on, so the form never opens for a refused signer.
        if (!user.HasRequiredNameFields)
            return RedirectToProfileEditForStub();

        var viewModel = await BuildConsentReviewViewModelAsync(id, user.Id, HttpContext.RequestAborted);
        if (viewModel is null)
            return NotFound();

        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(ConsentSubmitModel model)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        if (!model.ExplicitConsent)
        {
            return await RedisplayUncheckedConsentAsync(model.DocumentVersionId, user.Id);
        }

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = Request.Headers.UserAgent.ToString();

        ConsentSubmitResult result;
        try
        {
            result = await consentService.SubmitConsentAsync(
                user.Id, model.DocumentVersionId, model.ExplicitConsent,
                ipAddress, userAgent);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to submit consent for user {UserId}, document version {DocumentVersionId}",
                user.Id, model.DocumentVersionId);
            SetError(localizer["Consent_SubmitError"].Value);
            return RedirectToAction(nameof(Index));
        }

        if (string.Equals(result.ErrorKey, "StubProfile", StringComparison.Ordinal))
            return RedirectToProfileEditForStub();

        if (!result.Success)
        {
            SetConsentSubmitFailureFlash(result);
            return RedirectToAction(nameof(Index));
        }

        // ConsentService doesn't call Onboarding (the arrow is one-way), so the controller reports the signature. The
        // consent is already recorded: a failed report is logged, never shown as a failed signature.
        try
        {
            await onboardingService.ConsentSignedAsync(user.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Consent recorded but the onboarding report failed for user {UserId}", user.Id);
        }

        SetConsentSubmitSuccessFlash(result);
        return RedirectToAction(nameof(Index));
    }

    private async Task<IActionResult> RedisplayUncheckedConsentAsync(Guid documentVersionId, Guid userId)
    {
        ModelState.AddModelError(string.Empty, localizer["Consent_MustCheck"].Value);

        var viewModel = await BuildConsentReviewViewModelAsync(documentVersionId, userId);
        if (viewModel is null)
            return NotFound();

        return View(nameof(Review), viewModel);
    }

    private void SetConsentSubmitFailureFlash(ConsentSubmitResult result)
    {
        if (string.Equals(result.ErrorKey, "AlreadyConsented", StringComparison.Ordinal))
        {
            SetInfo(localizer["Consent_AlreadyConsented"].Value);
        }
        else
        {
            SetError(localizer["Consent_SubmitError"].Value);
        }
    }

    private void SetConsentSubmitSuccessFlash(ConsentSubmitResult result) =>
        SetSuccess(string.Format(localizer["Consent_ThankYou"].Value, result.DocumentName));

    private IActionResult RedirectToProfileEditForStub()
    {
        SetInfo(localizer["Consent_StubProfile_AddName"].Value);
        return RedirectToAction("Edit", "Profile");
    }

    private async Task<ConsentDetailViewModel?> BuildConsentReviewViewModelAsync(Guid documentVersionId, Guid userId, CancellationToken ct = default)
    {
        var detail = await consentService.GetConsentReviewDetailAsync(documentVersionId, userId, ct);

        if (detail is null)
        {
            return null;
        }

        return new ConsentDetailViewModel
        {
            DocumentVersionId = detail.DocumentVersionId,
            DocumentName = detail.DocumentName,
            VersionNumber = detail.VersionNumber,
            Content = new Dictionary<string, string>(detail.Content, StringComparer.Ordinal),
            EffectiveFrom = detail.EffectiveFrom.ToDateTimeUtc(),
            ChangesSummary = detail.ChangesSummary,
            HasAlreadyConsented = detail.HasAlreadyConsented,
            ConsentedByFullName = detail.UserFullName,
            ConsentedAt = detail.ConsentedAt?.ToDateTimeUtc()
        };
    }
}
