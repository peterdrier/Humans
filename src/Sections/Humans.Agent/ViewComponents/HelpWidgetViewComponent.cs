using Humans.Agent.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Agent.ViewComponents;

/// <summary>The chrome slot invokes it unconditionally, so the sign-in gate lives here.</summary>
internal sealed class HelpWidgetViewComponent(IAgentSettingsService agent) : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        if (UserClaimsPrincipal.Identity?.IsAuthenticated != true)
            return Content(string.Empty);

        var pagePath = Request.Path.Value ?? string.Empty;
        var agentAvailable = agent.Current.Enabled;

        return View(new HelpWidgetModel(pagePath, agentAvailable));
    }
}

internal sealed record HelpWidgetModel(string PagePath, bool AgentAvailable);
