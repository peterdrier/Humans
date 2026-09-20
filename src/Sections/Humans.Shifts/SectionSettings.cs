using Humans.Base.Authorization;
using Humans.Settings.Contracts;

namespace Humans.Shifts;

/// <summary>
/// Shifts' /Settings tab (peterdrier/Humans#1634): the knobs-only form
/// (<c>IsShiftBrowsingOpen</c>, <c>GlobalVolunteerCap</c>, <c>ReminderLeadTimeHours</c>)
/// moved off the now-retired <c>/Shifts/Settings</c> page.
/// </summary>
internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("shifts", "Settings_TabShifts", "ShiftsSettingsTab", PolicyNames.AdminOnly)
    ];
}
