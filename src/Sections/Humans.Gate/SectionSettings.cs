using Humans.Base.Authorization;
using Humans.Settings.Contracts;

namespace Humans.Gate;

/// <summary>
/// Gate's /Settings tab (peterdrier/Humans#1634): general-entry instant and minor age
/// threshold, moved off <c>/Gate/Admin</c> — Staff PIN admin stays there (see
/// <c>GateController.Admin</c>).
/// </summary>
internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("gate", "Settings_TabGate", "GateSettingsTab", PolicyNames.TicketAdminOrAdmin)
    ];
}
