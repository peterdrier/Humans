using Humans.Base.Authorization;
using Humans.Settings.Contracts;
using Humans.Shifts.ViewComponents;

namespace Humans.Shifts;

internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("shifts", "Settings_TabShifts", typeof(ShiftsSettingsTabViewComponent), PolicyNames.AdminOnly)
    ];
}
