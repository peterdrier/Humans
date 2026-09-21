using Humans.Agent.Models;
using Humans.Agent.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Agent.ViewComponents;

/// <summary>
/// The /Settings#agent tab (peterdrier/Humans#1634): wraps the same form that used to
/// live at /Agent/Admin/Settings. <see cref="SectionSettings"/> contributes this tab
/// with <c>PolicyNames.AdminOnly</c>, so composition already keeps a non-admin from
/// seeing it — no further check needed here.
/// </summary>
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
