using Humans.Application.Constants;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Consent;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Single floating "Help" widget that combines the previous
/// <c>IssuesWidget</c> + <c>AgentWidget</c> corner FABs into one menu with
/// two items: "Talk with AI Agent" (primary) and "Create issue" (secondary).
/// Authenticated users see the bubble; the agent option is shown whenever
/// the agent feature is enabled. The agent consent gate is rendered
/// in-place and only displayed when the user has not yet consented.
/// </summary>
public class HelpWidgetViewComponent : ViewComponent
{
    private readonly IAgentSettingsService _settings;
    private readonly IConsentService _consents;
    private readonly UserManager<Humans.Domain.Entities.User> _users;

    public HelpWidgetViewComponent(
        IAgentSettingsService settings,
        IConsentService consents,
        UserManager<Humans.Domain.Entities.User> users)
    {
        _settings = settings;
        _consents = consents;
        _users = users;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (UserClaimsPrincipal?.Identity?.IsAuthenticated != true)
            return Content(string.Empty);

        var pagePath = Request?.Path.Value ?? string.Empty;

        var agentAvailable = false;
        var agentHasConsent = false;

        if (_settings.Current.Enabled)
        {
            var user = await _users.GetUserAsync(UserClaimsPrincipal);
            if (user is not null)
            {
                var pending = await _consents.GetPendingDocumentNamesAsync(user.Id, HttpContext.RequestAborted);
                agentHasConsent = !pending.Any(name =>
                    string.Equals(name, LegalDocumentNames.AgentChatTerms, StringComparison.Ordinal));
                agentAvailable = true;
            }
        }

        return View(new HelpWidgetModel(pagePath, agentAvailable, agentHasConsent));
    }
}

public sealed record HelpWidgetModel(string PagePath, bool AgentAvailable, bool AgentHasConsented);
