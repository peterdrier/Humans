using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Web.Models;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize]
public class ConsentController(
    IUserService userService,
    IConsentService consentService,
    IOnboardingService onboardingService,
    IStringLocalizer<SharedResource> localizer,
    ILogger<ConsentController> logger) : HumansControllerBase(userService)
{
    private readonly IUserService _userService = userService;

    public async Task<IActionResult> Index()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var (groups, history) = await consentService.GetConsentDashboardAsync(user.Id);

        var teamGroups = groups
            .Select(g =>
            {
                var docViewModels = g.Documents.Select(d => new ConsentDocumentViewModel
                {
                    DocumentVersionId = d.DocumentVersionId,
                    DocumentName = d.DocumentName,
                    VersionNumber = d.VersionNumber,
                    EffectiveFrom = d.EffectiveFrom.ToDateTimeUtc(),
                    HasConsented = d.HasConsented,
                    ConsentedAt = d.ConsentedAt?.ToDateTimeUtc(),
                    ChangesSummary = d.ChangesSummary,
                    LastUpdated = d.LastUpdated?.ToDateTimeUtc()
                }).ToList();

                return new ConsentTeamGroupViewModel
                {
                    TeamId = g.TeamId,
                    TeamName = g.TeamName,
                    Documents = docViewModels
                        .OrderBy(d => d.HasConsented)
                        .ThenBy(d => d.DocumentName, StringComparer.Ordinal)
                        .ToList()
                };
            })
            .OrderBy(tg => tg.AllConsented)
            .ThenBy(tg => tg.TeamName, StringComparer.Ordinal)
            .ToList();

        var viewModel = new ConsentIndexViewModel
        {
            TeamGroups = teamGroups,
            ConsentHistory = history.Take(10).Select(c => new ConsentHistoryViewModel
            {
                DocumentVersionId = c.DocumentVersionId,
                DocumentName = c.DocumentName,
                VersionNumber = c.VersionNumber,
                ConsentedAt = c.ConsentedAt.ToDateTimeUtc()
            }).ToList()
        };

        return View(viewModel);
    }

    [HttpGet]
    public async Task<IActionResult> Review(Guid id)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        // Stub profile (no legal name) cannot attest to a consent. Bounce to /Profile/Edit.
        if (await IsStubProfileAsync(user.Id))
            return RedirectToProfileEditForStub();

        var viewModel = await BuildConsentReviewViewModelAsync(id, user.Id);
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

        if (await IsStubProfileAsync(user.Id))
            return RedirectToProfileEditForStub();

        if (!model.ExplicitConsent)
        {
            return await RedisplayUncheckedConsentAsync(model.DocumentVersionId, user.Id);
        }

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = Request.Headers.UserAgent.ToString();

        try
        {
            var result = await consentService.SubmitConsentAsync(
                user.Id, model.DocumentVersionId, model.ExplicitConsent,
                ipAddress, userAgent);

            if (!result.Success)
            {
                SetConsentSubmitFailureFlash(result);
                return RedirectToAction(nameof(Index));
            }

            // Peer-call: ConsentService does not call into Onboarding (avoids inverted arrow).
            await onboardingService.SetConsentCheckPendingIfEligibleAsync(user.Id);

            SetConsentSubmitSuccessFlash(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to submit consent for user {UserId}, document version {DocumentVersionId}",
                user.Id, model.DocumentVersionId);
            SetError(localizer["Consent_SubmitError"].Value);
        }
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
    }

    private void SetConsentSubmitSuccessFlash(ConsentSubmitResult result) =>
        SetSuccess(string.Format(localizer["Consent_ThankYou"].Value, result.DocumentName));

    private async Task<bool> IsStubProfileAsync(Guid userId)
    {
        var info = await _userService.GetUserInfoAsync(userId);
        return info is not null && info.IsStub;
    }

    private IActionResult RedirectToProfileEditForStub()
    {
        SetInfo(localizer["Consent_StubProfile_AddName"].Value);
        return RedirectToAction(nameof(ProfileController.Edit), "Profile");
    }

    private async Task<ConsentDetailViewModel?> BuildConsentReviewViewModelAsync(Guid documentVersionId, Guid userId)
    {
        var detail = await consentService.GetConsentReviewDetailAsync(documentVersionId, userId);

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
