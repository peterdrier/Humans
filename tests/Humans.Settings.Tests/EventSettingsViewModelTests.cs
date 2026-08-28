using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using Humans.Settings.Contracts;
using Humans.Settings.Models;
using NodaTime;

namespace Humans.Settings.Tests;

/// <summary>
/// The build window is <c>[BuildStartOffset, 0)</c> and the four sub-periods
/// partition it, so <c>BuildStartOffset ≤ FirstCrew &lt; SetupWeek &lt; PreEvent
/// &lt; FinishingWeekend &lt; 0</c>. The shipped defaults have to satisfy that.
/// </summary>
public sealed class EventSettingsViewModelTests
{
    private static IReadOnlyList<ValidationResult> Validate(EventSettingsViewModel model) =>
        [.. model.Validate(new ValidationContext(model))];

    [HumansFact]
    public void TheShippedDefaults_Validate()
    {
        Validate(new EventSettingsViewModel()).Should().BeEmpty();
    }

    [HumansFact]
    public void TheShippedDefaults_PutBuildStartOnTheFirstCrewDay()
    {
        var model = new EventSettingsViewModel();

        model.BuildStartOffset.Should().Be(model.FirstCrewStartOffset);
        model.FirstCrewStartOffset.Should().BeLessThan(model.SetupWeekStartOffset);
    }

    [HumansFact]
    public void ABuildStartAfterTheFirstCrewDay_IsRejectedOnBothFields()
    {
        var model = new EventSettingsViewModel { BuildStartOffset = -14 };

        var results = Validate(model);

        results.Should().ContainSingle()
            .Which.MemberNames.Should().BeEquivalentTo(
                nameof(EventSettingsViewModel.BuildStartOffset),
                nameof(EventSettingsViewModel.FirstCrewStartOffset));
    }

    [HumansFact]
    public void ABuildStartEarlierThanTheFirstCrewDay_IsAccepted()
    {
        // A gap below the first sub-period is legal — those days classify as
        // "pre-build"; only a first-crew boundary the build window has not
        // reached yet is broken.
        Validate(new EventSettingsViewModel { BuildStartOffset = -30 }).Should().BeEmpty();
    }

    [HumansFact]
    public void SubPeriodsOutOfOrder_AreRejected()
    {
        var model = new EventSettingsViewModel { SetupWeekStartOffset = -26 };

        Validate(model).Should().Contain(r => r.ErrorMessage!.Contains("strictly ascending"));
    }

    private static EventSettingsInfo CarriedRow() => new(
        Id: Guid.NewGuid(),
        EventName: "Nowhere 2026",
        Year: 2026,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: new LocalDate(2026, 7, 9),
        BuildStartOffset: -25,
        EventEndOffset: 6,
        StrikeEndOffset: 9,
        FirstCrewStartOffset: -25,
        SetupWeekStartOffset: -16,
        PreEventWeekStartOffset: -9,
        FinishingWeekendStartOffset: -4,
        EarlyEntryCapacity: new Dictionary<int, int> { [-14] = 50 },
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null,
        Status: EventSettingsStatus.Active);

    [HumansFact]
    public void Parse_DerivesYearFromTheGateOpeningDate()
    {
        var form = EventSettingsFormMapper.ToViewModel(CarriedRow());
        form.GateOpeningDate = "2027-07-08";

        var parsed = EventSettingsFormMapper.Parse(form);

        parsed.Success.Should().BeTrue();
        parsed.Settings!.Year.Should().Be(2027);
    }

    [HumansFact]
    public void ACarriedRow_SavesAnUnrelatedEditWithoutTouchingTheOffsets()
    {
        var carried = CarriedRow();
        var form = EventSettingsFormMapper.ToViewModel(carried);
        form.EventName = "Nowhere 2026 (renamed)";

        Validate(form).Should().BeEmpty();

        var parsed = EventSettingsFormMapper.Parse(form);

        parsed.Success.Should().BeTrue();
        parsed.Settings!.EventName.Should().Be("Nowhere 2026 (renamed)");
        parsed.Settings.BuildStartOffset.Should().Be(carried.BuildStartOffset);
        parsed.Settings.FirstCrewStartOffset.Should().Be(carried.FirstCrewStartOffset);
        parsed.Settings.SetupWeekStartOffset.Should().Be(carried.SetupWeekStartOffset);
        parsed.Settings.PreEventWeekStartOffset.Should().Be(carried.PreEventWeekStartOffset);
        parsed.Settings.FinishingWeekendStartOffset.Should().Be(carried.FinishingWeekendStartOffset);
    }
}
