using AwesomeAssertions;
using Humans.Shifts.Models;

namespace Humans.Shifts.Tests.Models;

/// <summary>
/// The event-settings form's two JSON fields are typed by an operator. Malformed
/// JSON is a field error on the form, never an unhandled exception.
/// </summary>
public sealed class EventSettingsFormMapperTests
{
    private static EventSettingsViewModel ValidModel() => new()
    {
        EventName = "Nowhere",
        TimeZoneId = "Europe/Madrid",
        GateOpeningDate = "2026-07-06",
    };

    [HumansFact]
    public void Parse_ValidJsonMaps_ProducesDraft()
    {
        var model = ValidModel();
        model.EarlyEntryCapacityJson = "{\"-5\": 10, \"-4\": 20}";
        model.BarriosEarlyEntryAllocationJson = "{\"-5\": 3}";

        var result = EventSettingsFormMapper.Parse(model);

        result.Success.Should().BeTrue();
        result.Draft!.EarlyEntryCapacity.Should().Equal(new Dictionary<int, int> { [-5] = 10, [-4] = 20 });
        result.Draft.BarriosEarlyEntryAllocation.Should().Equal(new Dictionary<int, int> { [-5] = 3 });
    }

    [HumansFact]
    public void Parse_EmptyJsonFields_DefaultToEmptyAndNull()
    {
        var model = ValidModel();
        model.EarlyEntryCapacityJson = "";
        model.BarriosEarlyEntryAllocationJson = null;

        var result = EventSettingsFormMapper.Parse(model);

        result.Success.Should().BeTrue();
        result.Draft!.EarlyEntryCapacity.Should().BeEmpty();
        result.Draft.BarriosEarlyEntryAllocation.Should().BeNull();
    }

    [HumansFact]
    public void Parse_MalformedCapacityJson_IsFieldError()
    {
        var model = ValidModel();
        model.EarlyEntryCapacityJson = "{not json";

        var result = EventSettingsFormMapper.Parse(model);

        result.Success.Should().BeFalse();
        result.Draft.Should().BeNull();
        result.Errors.Should().ContainSingle(e => e.FieldName == nameof(EventSettingsViewModel.EarlyEntryCapacityJson));
    }

    [HumansFact]
    public void Parse_MalformedBarriosJson_IsFieldError()
    {
        var model = ValidModel();
        model.BarriosEarlyEntryAllocationJson = "[1, 2]";

        var result = EventSettingsFormMapper.Parse(model);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.FieldName == nameof(EventSettingsViewModel.BarriosEarlyEntryAllocationJson));
    }
}
