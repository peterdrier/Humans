using Humans.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Single floating "Help" widget that combines the previous
/// <c>IssuesWidget</c> + <c>AgentWidget</c> corner FABs into one menu with
/// two items: "Talk with the Assistant" (primary) and "Create issue"
/// (secondary). Authenticated users see the bubble; the agent option is
/// shown whenever the agent feature is enabled. The Assistant panel
/// links to the AI Terms (<c>/Legal/agent-chat</c>) below the composer
/// instead of gating use behind explicit consent.
/// </summary>
public class HelpWidgetViewComponent(IAgentSettingsService settings) : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        if (UserClaimsPrincipal?.Identity?.IsAuthenticated != true)
            return Content(string.Empty);

        var pagePath = Request?.Path.Value ?? string.Empty;
        var agentAvailable = settings.Current.Enabled;

        return View(new HelpWidgetModel(pagePath, agentAvailable));
    }
}

public sealed record HelpWidgetModel(string PagePath, bool AgentAvailable);
