using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Humans.Shifts.Models;
using Humans.Shifts.Services.Dtos;
using NodaTime;

namespace Humans.Shifts.Tests.Models;

public sealed class EventSettingsFormMapperTests
{
    private static readonly BurnSettingsInfo Burn = new(
        Id: Guid.NewGuid(),
        EventName: "Nowhere 2026",
        Year: 2026,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: new LocalDate(2026, 7, 9),
        BuildStartOffset: -30,
        EventEndOffset: 6,
        StrikeEndOffset: 9,
        FirstCrewStartOffset: -28,
        SetupWeekStartOffset: -20,
        PreEventWeekStartOffset: -10,
        FinishingWeekendStartOffset: -3,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null,
        IsShiftBrowsingOpen: false);

    [HumansFact]
    public void ToViewModel_NoKnobsRow_ReturnsDefaultKnobsAndTheResolvedSubPeriods()
    {
        var vm = EventSettingsFormMapper.ToViewModel(null, Burn);

        vm.IsShiftBrowsingOpen.Should().BeFalse();
        vm.GlobalVolunteerCap.Should().BeNull();
        vm.ReminderLeadTimeHours.Should().Be(24);
        vm.FirstCrewStartOffset.Should().Be(-28);
        vm.SetupWeekStartOffset.Should().Be(-20);
        vm.PreEventWeekStartOffset.Should().Be(-10);
        vm.FinishingWeekendStartOffset.Should().Be(-3);
    }

    [HumansFact]
    public void ToViewModel_KnobsRowPresent_MapsItsFields()
    {
        var knobs = new ShiftEventKnobs(IsShiftBrowsingOpen: true, GlobalVolunteerCap: 300, ReminderLeadTimeHours: 12);

        var vm = EventSettingsFormMapper.ToViewModel(knobs, Burn);

        vm.IsShiftBrowsingOpen.Should().BeTrue();
        vm.GlobalVolunteerCap.Should().Be(300);
        vm.ReminderLeadTimeHours.Should().Be(12);
        vm.FirstCrewStartOffset.Should().Be(-28);
    }

    [HumansFact]
    public void Validate_TheShippedDefaultsPass()
    {
        new EventSettingsViewModel().Validate(new(new EventSettingsViewModel())).Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(-16, -16, -9, -4)]
    [Xunit.InlineData(-25, -16, -17, -4)]
    [Xunit.InlineData(-25, -16, -9, 0)]
    public void Validate_SubPeriodsOutOfOrderOrOnGateDay_AreRejected(int firstCrew, int setupWeek, int preEventWeek, int finishingWeekend)
    {
        var model = new EventSettingsViewModel
        {
            FirstCrewStartOffset = firstCrew,
            SetupWeekStartOffset = setupWeek,
            PreEventWeekStartOffset = preEventWeek,
            FinishingWeekendStartOffset = finishingWeekend,
        };

        model.Validate(new(model)).Should().ContainSingle().Which.ErrorMessage.Should().Contain("strictly ascending");
    }
}
