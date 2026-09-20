using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using Humans.Shifts.Models;
using NodaTime;

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

    /// <summary>
    /// The calendar (app-wide fields) is sourced from Settings via
    /// <c>ISettingsService</c>/<c>IBurnSettingsService</c>, never from this row's own
    /// columns, once one has been carried (nobodies-collective/Humans#1630).
    /// </summary>
    [HumansFact]
    public void ToViewModel_CalendarPresent_UsesCalendarNotEntityColumns()
    {
        var es = MakeEntity(eventName: "Stale Local Name", timeZoneId: "UTC");
        var calendar = MakeCalendar(es.Id, eventName: "Nowhere 2026", timeZoneId: "Europe/Madrid");

        var vm = EventSettingsFormMapper.ToViewModel(es, calendar);

        vm.EventName.Should().Be("Nowhere 2026");
        vm.TimeZoneId.Should().Be("Europe/Madrid");
        vm.IsShiftBrowsingOpen.Should().Be(es.IsShiftBrowsingOpen);
        vm.GlobalVolunteerCap.Should().Be(es.GlobalVolunteerCap);
        vm.ReminderLeadTimeHours.Should().Be(es.ReminderLeadTimeHours);
    }

    /// <summary>
    /// A brand-new row Settings hasn't carried yet has no calendar counterpart —
    /// the form falls back to the row's own columns only in that case.
    /// </summary>
    [HumansFact]
    public void ToViewModel_NoCalendar_FallsBackToEntityColumns()
    {
        var es = MakeEntity(eventName: "Brand New Burn", timeZoneId: "America/Los_Angeles");

        var vm = EventSettingsFormMapper.ToViewModel(es, null);

        vm.EventName.Should().Be("Brand New Burn");
        vm.TimeZoneId.Should().Be("America/Los_Angeles");
    }

    private static EventSettings MakeEntity(string eventName, string timeZoneId) => new()
    {
        Id = Guid.NewGuid(),
        EventName = eventName,
        Year = 2026,
        TimeZoneId = timeZoneId,
        GateOpeningDate = new LocalDate(2026, 7, 1),
        IsShiftBrowsingOpen = true,
        GlobalVolunteerCap = 300,
        ReminderLeadTimeHours = 24,
        IsActive = true,
    };

    private static BurnSettingsInfo MakeCalendar(Guid id, string eventName, string timeZoneId) => new(
        Id: id,
        EventName: eventName,
        Year: 2026,
        TimeZoneId: timeZoneId,
        GateOpeningDate: new LocalDate(2026, 7, 1),
        BuildStartOffset: -14,
        EventEndOffset: 6,
        StrikeEndOffset: 9,
        FirstCrewStartOffset: -10,
        SetupWeekStartOffset: -7,
        PreEventWeekStartOffset: -5,
        FinishingWeekendStartOffset: -4,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null,
        IsShiftBrowsingOpen: true);
}
