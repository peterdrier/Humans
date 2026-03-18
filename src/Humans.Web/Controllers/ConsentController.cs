using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Application;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

    [Authorize]
public class ConsentController : HumansControllerBase
{
    private readonly IConsentService _consentService;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public ConsentController(
        UserManager<User> userManager,
        IConsentService consentService,
        IStringLocalizer<SharedResource> localizer)
        : base(userManager)
    {
        _consentService = consentService;
        _localizer = localizer;
    }

    public async Task<IActionResult> Index()
    {
        var user = await GetCurrentUserAsync();
        if (user == null)
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
                    HasConsented = d.Consent != null,
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
        if (user == null)
            return NotFound();

        var (version, consentRecord, fullName) =
            await _consentService.GetConsentReviewDetailAsync(id, user.Id);

        if (version == null)
            return NotFound();

        var viewModel = new ConsentDetailViewModel
        {
            DocumentVersionId = version.Id,
            DocumentName = version.LegalDocument.Name,
            VersionNumber = version.VersionNumber,
            Content = new Dictionary<string, string>(version.Content, StringComparer.Ordinal),
            EffectiveFrom = version.EffectiveFrom.ToDateTimeUtc(),
            ChangesSummary = version.ChangesSummary,
            HasAlreadyConsented = consentRecord != null,
            ConsentedByFullName = fullName,
            ConsentedAt = consentRecord?.ConsentedAt.ToDateTimeUtc()
        };

        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(ConsentSubmitModel model)
    {
        var user = await GetCurrentUserAsync();
        if (user == null)
            return NotFound();

        if (!model.ExplicitConsent)
        {
            ModelState.AddModelError(string.Empty, _localizer["Consent_MustCheck"].Value);
            return RedirectToAction(nameof(Review), new { id = model.DocumentVersionId });
        }

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = Request.Headers.UserAgent.ToString();

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
        return RedirectToAction(nameof(Index));
    }
}
