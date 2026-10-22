using Humans.Base.Authorization;
using Humans.Settings.Contracts;
using Humans.Gate.ViewComponents;

namespace Humans.Gate;

internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("gate", "Settings_TabGate", typeof(GateSettingsTabViewComponent), PolicyNames.TicketAdminOrAdmin)
    ];
}
