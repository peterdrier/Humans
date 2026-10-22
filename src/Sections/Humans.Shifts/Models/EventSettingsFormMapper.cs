using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Models;

/// <summary>Shifts-owned knobs only; the app-wide calendar is Settings'.</summary>
internal static class EventSettingsFormMapper
{
    /// <summary>No knobs row is created until the form is saved.</summary>
    internal static EventSettingsViewModel ToViewModel(ShiftEventKnobs? knobs) => knobs is null
        ? new EventSettingsViewModel()
        : new EventSettingsViewModel
        {
            IsShiftBrowsingOpen = knobs.IsShiftBrowsingOpen,
            GlobalVolunteerCap = knobs.GlobalVolunteerCap,
            ReminderLeadTimeHours = knobs.ReminderLeadTimeHours,
        };
}
