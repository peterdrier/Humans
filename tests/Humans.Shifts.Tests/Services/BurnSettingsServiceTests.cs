using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using AwesomeAssertions;
using Humans.Settings.Contracts;
using Humans.Shifts.Services;
using NodaTime;
using NSubstitute;

namespace Humans.Shifts.Tests.Services;

/// <summary>
/// The calendar always comes from Settings via <see cref="EventCalendarResolver"/>;
/// this section's own repo only supplies the knobs row, which may not exist yet
/// (nobodies-collective/Humans#1631).
/// </summary>
public sealed class BurnSettingsServiceTests
{
    private readonly IShiftManagementRepository _repo = Substitute.For<IShiftManagementRepository>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly BurnSettingsService _service;

    public BurnSettingsServiceTests()
    {
        _service = new BurnSettingsService(_repo, new EventCalendarResolver(_settingsService));
    }

    private void StubActiveCalendar(EventSettingsInfo info) =>
        _settingsService.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(info);

    private void StubCalendarById(EventSettingsInfo info) =>
        _settingsService.GetEventSettingsByIdAsync(info.Id, Arg.Any<CancellationToken>()).Returns(info);

    [HumansFact]
    public async Task GetByIdAsync_MapsCalendarToDto()
    {
        var id = Guid.NewGuid();
        var calendar = NewCalendar(id);
        StubCalendarById(calendar);
        _repo.GetEventSettingsByIdAsync(id, Arg.Any<CancellationToken>()).Returns((EventSettings?)null);

        var result = await _service.GetByIdAsync(id, Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Id.Should().Be(id);
        result.EventName.Should().Be(calendar.EventName);
        result.TimeZoneId.Should().Be(calendar.TimeZoneId);
        result.GateOpeningDate.Should().Be(calendar.GateOpeningDate);
    }

    [HumansFact]
    public async Task GetByIdAsync_ReturnsNull_WhenSettingsHasNoSuchCalendar()
    {
        var result = await _service.GetByIdAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetActiveAsync_ReturnsNull_WhenSettingsHasNoActiveEvent()
    {
        var result = await _service.GetActiveAsync(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetActiveAsync_NoLocalKnobsRowYet_DefaultsIsShiftBrowsingOpenToFalse()
    {
        var calendar = NewCalendar(Guid.NewGuid());
        StubActiveCalendar(calendar);
        _repo.GetEventSettingsByIdAsync(calendar.Id, Arg.Any<CancellationToken>()).Returns((EventSettings?)null);

        var result = await _service.GetActiveAsync(Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Id.Should().Be(calendar.Id);
        result.IsShiftBrowsingOpen.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetActiveAsync_LocalKnobsRowExists_UsesItsIsShiftBrowsingOpen()
    {
        var calendar = NewCalendar(Guid.NewGuid());
        StubActiveCalendar(calendar);
        _repo.GetEventSettingsByIdAsync(calendar.Id, Arg.Any<CancellationToken>())
            .Returns(new EventSettings { Id = calendar.Id, IsShiftBrowsingOpen = true });

        var result = await _service.GetActiveAsync(Xunit.TestContext.Current.CancellationToken);

        result!.IsShiftBrowsingOpen.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetEarlyEntryCapacityForDay_OnReturnedDto_PerformsStepFunctionLookup()
    {
        var calendar = NewCalendar(Guid.NewGuid()) with
        {
            EarlyEntryCapacity = new Dictionary<int, int> { [-10] = 5, [-5] = 12 },
        };
        StubActiveCalendar(calendar);
        _repo.GetEventSettingsByIdAsync(calendar.Id, Arg.Any<CancellationToken>()).Returns((EventSettings?)null);

        var result = await _service.GetActiveAsync(Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.GetEarlyEntryCapacityForDay(-11).Should().Be(0);
        result.GetEarlyEntryCapacityForDay(-10).Should().Be(5);
        result.GetEarlyEntryCapacityForDay(-7).Should().Be(5);
        result.GetEarlyEntryCapacityForDay(-5).Should().Be(12);
        result.GetEarlyEntryCapacityForDay(0).Should().Be(12);
    }

    private static EventSettingsInfo NewCalendar(Guid id) => new(
        Id: id,
        EventName: "Nowhere 2026",
        Year: 2026,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: new LocalDate(2026, 7, 1),
        BuildStartOffset: -14,
        EventEndOffset: 6,
        StrikeEndOffset: 9,
        FirstCrewStartOffset: -25,
        SetupWeekStartOffset: -16,
        PreEventWeekStartOffset: -9,
        FinishingWeekendStartOffset: -4,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null);
}
