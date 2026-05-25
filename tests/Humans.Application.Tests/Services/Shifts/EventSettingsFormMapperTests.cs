using AwesomeAssertions;
using Humans.Web.Models;
using Humans.Web.Models.Shifts;
using NodaTime;

namespace Humans.Application.Tests.Services.Shifts;

public class EventSettingsFormMapperTests
{
    [HumansFact]
    public void Parse_valid_model_creates_draft_and_entity()
    {
        var model = ValidModel();

        var parsed = EventSettingsFormMapper.Parse(model);
        var entity = EventSettingsFormMapper.Create(parsed.Draft!, Instant.FromUtc(2026, 5, 15, 12, 0));

        parsed.Success.Should().BeTrue();
        entity.EventName.Should().Be("Humans 2026");
        entity.TimeZoneId.Should().Be("Europe/Madrid");
        entity.GateOpeningDate.Should().Be(new LocalDate(2026, 7, 1));
        entity.Year.Should().Be(2026);
        entity.EarlyEntryCapacity.Should().ContainKey(-3).WhoseValue.Should().Be(12);
        entity.BarriosEarlyEntryAllocation.Should().ContainKey(-2).WhoseValue.Should().Be(5);
        entity.EarlyEntryClose.Should().Be(Instant.FromUtc(2026, 6, 1, 0, 0));
        entity.CreatedAt.Should().Be(Instant.FromUtc(2026, 5, 15, 12, 0));
    }

    [HumansFact]
    public void Parse_invalid_temporal_fields_returns_field_errors()
    {
        var model = ValidModel();
        model.TimeZoneId = "Not/AZone";
        model.GateOpeningDate = "not-a-date";
        model.EarlyEntryClose = "not-an-instant";

        var parsed = EventSettingsFormMapper.Parse(model);

        parsed.Success.Should().BeFalse();
        parsed.Errors.Select(e => e.FieldName).Should().BeEquivalentTo(
            nameof(EventSettingsViewModel.TimeZoneId),
            nameof(EventSettingsViewModel.GateOpeningDate),
            nameof(EventSettingsViewModel.EarlyEntryClose));
    }

    private static EventSettingsViewModel ValidModel() => new()
    {
        EventName = "Humans 2026",
        TimeZoneId = "Europe/Madrid",
        GateOpeningDate = "2026-07-01",
        BuildStartOffset = -14,
        EventEndOffset = 6,
        StrikeEndOffset = 9,
        FirstCrewStartOffset = -10,
        SetupWeekStartOffset = -7,
        PreEventWeekStartOffset = -5,
        FinishingWeekendStartOffset = 4,
        EarlyEntryCapacityJson = """{"-3":12}""",
        BarriosEarlyEntryAllocationJson = """{"-2":5}""",
        EarlyEntryClose = "2026-06-01T00:00:00Z",
        IsShiftBrowsingOpen = true,
        GlobalVolunteerCap = 300,
        ReminderLeadTimeHours = 24,
        IsActive = true
    };
}
