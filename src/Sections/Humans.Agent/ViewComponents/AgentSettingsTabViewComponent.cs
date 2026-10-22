using Humans.Agent.Models;
using Humans.Agent.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Agent.ViewComponents;

/// <summary>No check here: the tab is contributed with AdminOnly, so composition already hides it.</summary>
internal sealed class AgentSettingsTabViewComponent(IAgentSettingsService settings) : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var s = settings.Current;
        return View(new AdminAgentSettingsViewModel
        {
            Enabled = s.Enabled,
            Model = s.Model,
            PreloadConfig = s.PreloadConfig,
            DailyMessageCap = s.DailyMessageCap,
            HourlyMessageCap = s.HourlyMessageCap,
            DailyTokenCap = s.DailyTokenCap,
            RetentionDays = s.RetentionDays
        });
    }
}
