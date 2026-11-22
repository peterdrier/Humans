using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using Humans.Settings.Contracts;
using Humans.Settings.Models;
using NodaTime;

namespace Humans.Settings.Tests;

/// <summary>
/// Window order and the early-entry range are the service's rules (<c>ServiceTests</c>); the form only
/// binds and parses.
/// </summary>
public sealed class EventSettingsViewModelTests
{
    private static IReadOnlyList<ValidationResult> Validate(EventSettingsViewModel model)
    {
        List<ValidationResult> results = [];
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }

    /// <summary>The defaults plus the `[Required]` fields the operator always fills in.</summary>
    private static EventSettingsViewModel FilledForm() => new()
    {
        EventName = "Nowhere 2026",
        GateOpeningDate = "2026-07-09",
    };

    [HumansFact]
    public void TheShippedDefaults_PassFullModelValidation()
    {
        Validate(FilledForm()).Should().BeEmpty();
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
        EarlyEntryCapacity: new Dictionary<int, int> { [-14] = 50 },
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null,
        Status: EventSettingsStatus.Active);

    [HumansFact]
    public void IsOffSeason_RoundTripsThroughTheForm()
    {
        var form = EventSettingsFormMapper.ToViewModel(SavedRow() with { IsOffSeason = true });
        form.IsOffSeason.Should().BeTrue();

        var parsed = EventSettingsFormMapper.Parse(form);

        parsed.Success.Should().BeTrue();
        parsed.Settings!.IsOffSeason.Should().BeTrue();
    }

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
    public void ASavedRow_PreservesGlobalBuildStartAcrossAnUnrelatedEdit()
    {
        var saved = SavedRow();
        var form = EventSettingsFormMapper.ToViewModel(saved);
        form.EventName = "Nowhere 2026 (renamed)";

        var parsed = EventSettingsFormMapper.Parse(form);

        parsed.Success.Should().BeTrue();
        parsed.Settings!.EventName.Should().Be("Nowhere 2026 (renamed)");
        parsed.Settings.BuildStartOffset.Should().Be(saved.BuildStartOffset);
    }
}
