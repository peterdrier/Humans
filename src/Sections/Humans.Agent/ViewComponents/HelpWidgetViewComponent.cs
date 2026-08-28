using Humans.Agent.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Agent.ViewComponents;

/// <summary>
/// Single floating "Help" widget: one menu with two items, "Talk with the
/// Assistant" (primary) and "Create issue" (secondary).
/// Authenticated users see the bubble; the agent option is
/// shown whenever the agent feature is enabled. The Assistant panel
/// links to the AI Terms (<c>/Legal/agent-chat</c>) below the composer
/// instead of gating use behind explicit consent. Contributed into
/// Shell's <c>body-end</c> chrome slot (nobodies-collective/Humans#1091) —
/// the chrome slot invokes unconditionally, so the auth gate lives here.
/// </summary>
internal sealed class HelpWidgetViewComponent(IAgentAvailability agent) : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        if (UserClaimsPrincipal?.Identity?.IsAuthenticated != true)
            return Content(string.Empty);

        var pagePath = Request?.Path.Value ?? string.Empty;
        var agentAvailable = agent.IsEnabled;

        return View(new HelpWidgetModel(pagePath, agentAvailable));
    }
}

internal sealed record HelpWidgetModel(string PagePath, bool AgentAvailable);
