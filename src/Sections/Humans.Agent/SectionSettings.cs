using Humans.Base.Authorization;
using Humans.Settings.Contracts;
using Humans.Agent.ViewComponents;

namespace Humans.Agent;

internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("agent", "Settings_TabAgent", typeof(AgentSettingsTabViewComponent), PolicyNames.AdminOnly)
    ];
}
