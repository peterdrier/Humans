using Humans.Shifts.Contracts;
using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Models;

/// <summary>Shifts-owned knobs and build sub-periods only; the app-wide calendar is Settings'.</summary>
internal static class EventSettingsFormMapper
{
    /// <summary>
    /// No knobs row is created until the form is saved. The sub-periods come from <paramref name="burn"/>, which
    /// already falls back to Settings' values where Shifts has none.
    /// </summary>
    internal static EventSettingsViewModel ToViewModel(ShiftEventKnobs? knobs, BurnSettingsInfo burn)
    {
        var model = knobs is null
            ? new EventSettingsViewModel()
            : new EventSettingsViewModel
            {
                IsShiftBrowsingOpen = knobs.IsShiftBrowsingOpen,
                GlobalVolunteerCap = knobs.GlobalVolunteerCap,
                ReminderLeadTimeHours = knobs.ReminderLeadTimeHours,
            };
        model.FirstCrewStartOffset = burn.FirstCrewStartOffset;
        model.SetupWeekStartOffset = burn.SetupWeekStartOffset;
        model.PreEventWeekStartOffset = burn.PreEventWeekStartOffset;
        model.FinishingWeekendStartOffset = burn.FinishingWeekendStartOffset;
        return model;
    }
}
