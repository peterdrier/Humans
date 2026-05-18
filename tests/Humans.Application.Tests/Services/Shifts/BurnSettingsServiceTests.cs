using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Shifts;
using Humans.Domain.Entities;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Shifts;

public sealed class BurnSettingsServiceTests
{
    private readonly IShiftManagementRepository _repo = Substitute.For<IShiftManagementRepository>();
    private readonly BurnSettingsService _service;

    public BurnSettingsServiceTests()
    {
        _service = new BurnSettingsService(_repo);
    }

    [HumansFact]
    public async Task GetByIdAsync_MapsEntityToDto()
    {
        var id = Guid.NewGuid();
        var entity = NewEventSettings(id);
        _repo.GetEventSettingsByIdAsync(id, Arg.Any<CancellationToken>()).Returns(entity);

        var result = await _service.GetByIdAsync(id);

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

        var result = await _service.GetByIdAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetActiveAsync_MapsEntityToDto()
    {
        var entity = NewEventSettings(Guid.NewGuid());
        _repo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(entity);

        var result = await _service.GetActiveAsync();

        result.Should().NotBeNull();
        result.Id.Should().Be(entity.Id);
        await _repo.Received(1).GetActiveEventSettingsAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetActiveAsync_ReturnsNull_WhenRepositoryReturnsNull()
    {
        _repo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns((EventSettings?)null);

        var result = await _service.GetActiveAsync();

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetEarlyEntryCapacityForDay_OnReturnedDto_PerformsStepFunctionLookup()
    {
        var entity = NewEventSettings(Guid.NewGuid());
        entity.EarlyEntryCapacity[-10] = 5;
        entity.EarlyEntryCapacity[-5] = 12;
        _repo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(entity);

        var result = await _service.GetActiveAsync();

        result.Should().NotBeNull();
        result.GetEarlyEntryCapacityForDay(-11).Should().Be(0);
        result.GetEarlyEntryCapacityForDay(-10).Should().Be(5);
        result.GetEarlyEntryCapacityForDay(-7).Should().Be(5);
        result.GetEarlyEntryCapacityForDay(-5).Should().Be(12);
        result.GetEarlyEntryCapacityForDay(0).Should().Be(12);
    }

    [HumansFact]
    public async Task PropagatesCancellationTokenToRepository()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        await _service.GetByIdAsync(Guid.NewGuid(), token);
        await _service.GetActiveAsync(token);

        await _repo.Received(1).GetEventSettingsByIdAsync(Arg.Any<Guid>(), token);
        await _repo.Received(1).GetActiveEventSettingsAsync(token);
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
}
