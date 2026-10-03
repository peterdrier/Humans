using Humans.Base.Authorization;
using Humans.Settings.Contracts;
using Humans.Agent.ViewComponents;

namespace Humans.Agent;

/// <summary>
/// Agent's /Settings tab (peterdrier/Humans#1634).
/// </summary>
internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("agent", "Settings_TabAgent", typeof(AgentSettingsTabViewComponent), PolicyNames.AdminOnly)
    ];
}
