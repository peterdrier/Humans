using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using AwesomeAssertions;
using Humans.Settings.Contracts;
using Humans.Shifts.Services;
using NodaTime;
using NSubstitute;

namespace Humans.Shifts.Tests.Services;

public sealed class BurnSettingsServiceTests
{
    private readonly IShiftManagementRepository _repo = Substitute.For<IShiftManagementRepository>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly BurnSettingsService _service;

    public BurnSettingsServiceTests()
    {
        _service = new BurnSettingsService(_repo, new EventCalendarResolver(_settingsService));
    }

    /// <summary>
    /// Wires the calendar mock to answer for <paramref name="entity"/>'s id (and as the
    /// active row, if it's the active one) — a separate mock from <c>_repo</c>, same as
    /// production where the calendar comes from Settings, not from this section's repo.
    /// </summary>
    private void StubSettings(EventSettings entity)
    {
        var info = ToEventSettingsInfo(entity);
        _settingsService.GetEventSettingsByIdAsync(entity.Id, Arg.Any<CancellationToken>()).Returns(info);
        if (entity.IsActive)
            _settingsService.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(info);
    }

    [HumansFact]
    public async Task GetByIdAsync_MapsEntityToDto()
    {
        var id = Guid.NewGuid();
        var entity = NewEventSettings(id);
        _repo.GetEventSettingsByIdAsync(id, Arg.Any<CancellationToken>()).Returns(entity);
        StubSettings(entity);

        var result = await _service.GetByIdAsync(id, Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Id.Should().Be(id);
        result.EventName.Should().Be(entity.EventName);
        result.TimeZoneId.Should().Be(entity.TimeZoneId);
        result.GateOpeningDate.Should().Be(entity.GateOpeningDate);
        await _repo.Received(1).GetEventSettingsByIdAsync(id, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetByIdAsync_ReturnsNull_WhenRepositoryReturnsNull()
    {
        _repo.GetEventSettingsByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((EventSettings?)null);

        var result = await _service.GetByIdAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetActiveAsync_MapsEntityToDto()
    {
        var entity = NewEventSettings(Guid.NewGuid());
        _repo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(entity);
        StubSettings(entity);

        var result = await _service.GetActiveAsync(Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Id.Should().Be(entity.Id);
        await _repo.Received(1).GetActiveEventSettingsAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetActiveAsync_ReturnsNull_WhenRepositoryReturnsNull()
    {
        _repo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns((EventSettings?)null);

        var result = await _service.GetActiveAsync(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetEarlyEntryCapacityForDay_OnReturnedDto_PerformsStepFunctionLookup()
    {
        var entity = NewEventSettings(Guid.NewGuid());
        entity.EarlyEntryCapacity[-10] = 5;
        entity.EarlyEntryCapacity[-5] = 12;
        _repo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(entity);
        StubSettings(entity);

        var result = await _service.GetActiveAsync(Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.GetEarlyEntryCapacityForDay(-11).Should().Be(0);
        result.GetEarlyEntryCapacityForDay(-10).Should().Be(5);
        result.GetEarlyEntryCapacityForDay(-7).Should().Be(5);
        result.GetEarlyEntryCapacityForDay(-5).Should().Be(12);
        result.GetEarlyEntryCapacityForDay(0).Should().Be(12);
    }

    private static EventSettings NewEventSettings(Guid id) => new()
    {
        Id = id,
        EventName = "Nowhere 2026",
        Year = 2026,
        TimeZoneId = "Europe/Madrid",
        GateOpeningDate = new LocalDate(2026, 7, 1),
        IsActive = true,
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
    };

    private static EventSettingsInfo ToEventSettingsInfo(EventSettings src) => new(
        Id: src.Id,
        EventName: src.EventName,
        Year: src.Year,
        TimeZoneId: src.TimeZoneId,
        GateOpeningDate: src.GateOpeningDate,
        BuildStartOffset: src.BuildStartOffset,
        EventEndOffset: src.EventEndOffset,
        StrikeEndOffset: src.StrikeEndOffset,
        FirstCrewStartOffset: src.FirstCrewStartOffset,
        SetupWeekStartOffset: src.SetupWeekStartOffset,
        PreEventWeekStartOffset: src.PreEventWeekStartOffset,
        FinishingWeekendStartOffset: src.FinishingWeekendStartOffset,
        EarlyEntryCapacity: new Dictionary<int, int>(src.EarlyEntryCapacity),
        BarriosEarlyEntryAllocation: src.BarriosEarlyEntryAllocation is null
            ? null : new Dictionary<int, int>(src.BarriosEarlyEntryAllocation),
        EarlyEntryClose: src.EarlyEntryClose);
}
