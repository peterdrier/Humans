using AwesomeAssertions;
using Humans.Shifts.Models;
using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Tests.Models;

/// <summary>
/// The form is knobs-only now — the calendar lives in Settings
/// (nobodies-collective/Humans#1631).
/// </summary>
public sealed class EventSettingsFormMapperTests
{
    [HumansFact]
    public void ToViewModel_NoKnobsRow_ReturnsDefaults()
    {
        var vm = EventSettingsFormMapper.ToViewModel(null);

        vm.IsShiftBrowsingOpen.Should().BeFalse();
        vm.GlobalVolunteerCap.Should().BeNull();
        vm.ReminderLeadTimeHours.Should().Be(24);
    }

    [HumansFact]
    public void ToViewModel_KnobsRowPresent_MapsItsFields()
    {
        var knobs = new ShiftEventKnobs(IsShiftBrowsingOpen: true, GlobalVolunteerCap: 300, ReminderLeadTimeHours: 12);

        var vm = EventSettingsFormMapper.ToViewModel(knobs);

        vm.IsShiftBrowsingOpen.Should().BeTrue();
        vm.GlobalVolunteerCap.Should().Be(300);
        vm.ReminderLeadTimeHours.Should().Be(12);
    }
}
