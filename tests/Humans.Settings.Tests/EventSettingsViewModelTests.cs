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

    /// <summary>
    /// The path MVC actually takes: attributes first, then <c>Validate</c>. <see cref="Validate"/>
    /// alone never runs the <c>[Range]</c>s, so the "&lt; 0" half of the sub-period rule is
    /// invisible to it.
    /// </summary>
    private static IReadOnlyList<ValidationResult> ValidateFully(EventSettingsViewModel model)
    {
        List<ValidationResult> results = [];
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }

    /// <summary>The defaults plus the three `[Required]` fields the operator always fills in.</summary>
    private static EventSettingsViewModel FilledForm() => new()
    {
        EventName = "Nowhere 2026",
        TimeZoneId = "Europe/Madrid",
        GateOpeningDate = "2026-07-09",
    };

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
    public void TheShippedDefaults_PassFullModelValidation()
    {
        ValidateFully(FilledForm()).Should().BeEmpty();
    }

    [HumansFact]
    public void ASubPeriodOnOrAfterGateDay_IsRejectedByTheFieldRange()
    {
        // Ordering still holds (-9 < 1), so only the [Range] can catch this — and only
        // under full model validation.
        var model = FilledForm();
        model.FinishingWeekendStartOffset = 1;

        ValidateFully(model).Should().ContainSingle()
            .Which.MemberNames.Should().ContainSingle(
                nameof(EventSettingsViewModel.FinishingWeekendStartOffset));
    }

    [HumansFact]
    public void SubPeriodsOutOfOrder_AreRejected()
    {
        var model = new EventSettingsViewModel { SetupWeekStartOffset = -26 };

        Validate(model).Should().Contain(r => r.ErrorMessage!.Contains("strictly ascending"));
    }

    private static EventSettingsInfo SavedRow() => new(
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
        var form = EventSettingsFormMapper.ToViewModel(SavedRow());
        form.GateOpeningDate = "2027-07-08";

        var parsed = EventSettingsFormMapper.Parse(form);

        parsed.Success.Should().BeTrue();
        parsed.Settings!.Year.Should().Be(2027);
    }

    [HumansFact]
    public void ASavedRow_SavesAnUnrelatedEditWithoutTouchingTheOffsets()
    {
        var saved = SavedRow();
        var form = EventSettingsFormMapper.ToViewModel(saved);
        form.EventName = "Nowhere 2026 (renamed)";

        Validate(form).Should().BeEmpty();

        var parsed = EventSettingsFormMapper.Parse(form);

        parsed.Success.Should().BeTrue();
        parsed.Settings!.EventName.Should().Be("Nowhere 2026 (renamed)");
        parsed.Settings.BuildStartOffset.Should().Be(saved.BuildStartOffset);
        parsed.Settings.FirstCrewStartOffset.Should().Be(saved.FirstCrewStartOffset);
        parsed.Settings.SetupWeekStartOffset.Should().Be(saved.SetupWeekStartOffset);
        parsed.Settings.PreEventWeekStartOffset.Should().Be(saved.PreEventWeekStartOffset);
        parsed.Settings.FinishingWeekendStartOffset.Should().Be(saved.FinishingWeekendStartOffset);
    }

    // ── EarlyEntryStartOffset: BuildStartOffset ≤ offset < 0, null until configured.

    [HumansFact]
    public void NullEarlyEntryStartOffset_IsAccepted()
    {
        Validate(new EventSettingsViewModel { EarlyEntryStartOffset = null }).Should().BeEmpty();
    }

    [HumansFact]
    public void EarlyEntryStartOffset_BeforeBuildStart_IsRejected()
    {
        // BuildStartOffset defaults to -25.
        var model = new EventSettingsViewModel { EarlyEntryStartOffset = -26 };

        Validate(model).Should().ContainSingle()
            .Which.MemberNames.Should().ContainSingle(nameof(EventSettingsViewModel.EarlyEntryStartOffset));
    }

    [HumansFact]
    public void EarlyEntryStartOffset_AtZero_IsRejected()
    {
        var model = new EventSettingsViewModel { EarlyEntryStartOffset = 0 };

        Validate(model).Should().ContainSingle()
            .Which.MemberNames.Should().ContainSingle(nameof(EventSettingsViewModel.EarlyEntryStartOffset));
    }

    [HumansFact]
    public void EarlyEntryStartOffset_WithinRange_IsAccepted()
    {
        var model = new EventSettingsViewModel { EarlyEntryStartOffset = -7 };

        Validate(model).Should().BeEmpty();
    }
}
