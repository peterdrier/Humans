using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Domain.Entities;
using Humans.Web.Models;
using Humans.Application.Interfaces.Consent;

namespace Humans.Web.Controllers;

[Authorize]
public class ConsentController : HumansControllerBase
{
    private readonly IConsentService _consentService;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<ConsentController> _logger;

    public ConsentController(
        UserManager<User> userManager,
        IConsentService consentService,
        IStringLocalizer<SharedResource> localizer,
        ILogger<ConsentController> logger)
        : base(userManager)
    {
        _consentService = consentService;
        _localizer = localizer;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        var (groups, history) = await _consentService.GetConsentDashboardAsync(user.Id);

        var teamGroups = groups
            .Select(g =>
            {
                var docViewModels = g.Documents.Select(d => new ConsentDocumentViewModel
                {
                    DocumentVersionId = d.Version.Id,
                    DocumentName = d.Version.LegalDocument.Name,
                    VersionNumber = d.Version.VersionNumber,
                    EffectiveFrom = d.Version.EffectiveFrom.ToDateTimeUtc(),
                    HasConsented = d.Consent is not null,
                    ConsentedAt = d.Consent?.ConsentedAt.ToDateTimeUtc(),
                    ChangesSummary = d.Version.ChangesSummary,
                    LastUpdated = d.Version.LegalDocument.LastSyncedAt != default
                        ? d.Version.LegalDocument.LastSyncedAt.ToDateTimeUtc() : null
                }).ToList();

                return new ConsentTeamGroupViewModel
                {
                    TeamId = g.Team.Id,
                    TeamName = g.Team.Name,
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
                DocumentName = c.DocumentVersion.LegalDocument.Name,
                VersionNumber = c.DocumentVersion.VersionNumber,
                ConsentedAt = c.ConsentedAt.ToDateTimeUtc()
            }).ToList()
        };

        return View(viewModel);
    }

    [HttpGet]
    public async Task<IActionResult> Review(Guid id)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        var viewModel = await BuildConsentReviewViewModelAsync(id, user.Id);
        if (viewModel is null)
            return NotFound();

        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(ConsentSubmitModel model)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        if (!model.ExplicitConsent)
        {
            ModelState.AddModelError(string.Empty, _localizer["Consent_MustCheck"].Value);

            var viewModel = await BuildConsentReviewViewModelAsync(model.DocumentVersionId, user.Id);
            if (viewModel is null)
                return NotFound();

            return View(nameof(Review), viewModel);
        }

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = Request.Headers.UserAgent.ToString();

        try
        {
            var result = await _consentService.SubmitConsentAsync(
                user.Id, model.DocumentVersionId, model.ExplicitConsent,
                ipAddress, userAgent);

            if (!result.Success)
            {
                if (string.Equals(result.ErrorKey, "AlreadyConsented", StringComparison.Ordinal))
                    SetInfo(_localizer["Consent_AlreadyConsented"].Value);
                return RedirectToAction(nameof(Index));
            }

            SetSuccess(string.Format(_localizer["Consent_ThankYou"].Value, result.DocumentName));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit consent for user {UserId}, document version {DocumentVersionId}",
                user.Id, model.DocumentVersionId);
            SetError(_localizer["Consent_SubmitError"].Value);
        }
        return RedirectToAction(nameof(Index));
    }

    private async Task<ConsentDetailViewModel?> BuildConsentReviewViewModelAsync(Guid documentVersionId, Guid userId)
    {
        var (version, consentRecord, fullName) =
            await _consentService.GetConsentReviewDetailAsync(documentVersionId, userId);

        if (version is null)
        {
            return null;
        }

        return new ConsentDetailViewModel
        {
            DocumentVersionId = version.Id,
            DocumentName = version.LegalDocument.Name,
            VersionNumber = version.VersionNumber,
            Content = new Dictionary<string, string>(version.Content, StringComparer.Ordinal),
            EffectiveFrom = version.EffectiveFrom.ToDateTimeUtc(),
            ChangesSummary = version.ChangesSummary,
            HasAlreadyConsented = consentRecord is not null,
            ConsentedByFullName = fullName,
            ConsentedAt = consentRecord?.ConsentedAt.ToDateTimeUtc()
        };
    }
}
