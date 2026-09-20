using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Models;

/// <summary>
/// Form ↔ the Shifts-owned knobs for a <c>settings_event</c> row. The app-wide
/// calendar lives in Settings and is not mapped here (nobodies-collective/Humans#1631).
/// </summary>
internal static class EventSettingsFormMapper
{
    /// <summary>Defaults for an event with no Shifts knobs row yet — none created until saved.</summary>
    internal static EventSettingsViewModel ToViewModel(ShiftEventKnobs? knobs) => knobs is null
        ? new EventSettingsViewModel()
        : new EventSettingsViewModel
        {
            IsShiftBrowsingOpen = knobs.IsShiftBrowsingOpen,
            GlobalVolunteerCap = knobs.GlobalVolunteerCap,
            ReminderLeadTimeHours = knobs.ReminderLeadTimeHours,
        };
}
