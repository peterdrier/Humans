using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Shifts;
using Humans.Domain.Entities;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Shifts;

public sealed class EventSettingsServiceTests
{
    private readonly IShiftManagementRepository _repo = Substitute.For<IShiftManagementRepository>();
    private readonly EventSettingsService _service;

    public EventSettingsServiceTests()
    {
        _service = new EventSettingsService(_repo);
    }

    [HumansFact]
    public async Task GetByIdAsync_DelegatesToRepository()
    {
        var id = Guid.NewGuid();
        var settings = NewEventSettings(id);
        _repo.GetEventSettingsByIdAsync(id, Arg.Any<CancellationToken>()).Returns(settings);

        var result = await _service.GetByIdAsync(id);

        result.Should().BeSameAs(settings);
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
    public async Task GetActiveAsync_DelegatesToRepository()
    {
        var settings = NewEventSettings(Guid.NewGuid());
        _repo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var result = await _service.GetActiveAsync();

        result.Should().BeSameAs(settings);
        await _repo.Received(1).GetActiveEventSettingsAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetActiveOptionsAsync_ReturnsEmpty_WhenNoActiveSettings()
    {
        _repo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns((EventSettings?)null);

        var result = await _service.GetActiveOptionsAsync();

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetActiveOptionsAsync_ReturnsSingleton_WhenActiveSettingsExist()
    {
        var settings = NewEventSettings(Guid.NewGuid());
        _repo.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var result = await _service.GetActiveOptionsAsync();

        result.Should().ContainSingle().Which.Should().BeSameAs(settings);
    }

    [HumansFact]
    public async Task PropagatesCancellationTokenToRepository()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        await _service.GetByIdAsync(Guid.NewGuid(), token);
        await _service.GetActiveAsync(token);
        await _service.GetActiveOptionsAsync(token);

        await _repo.Received(1).GetEventSettingsByIdAsync(Arg.Any<Guid>(), token);
        await _repo.Received(2).GetActiveEventSettingsAsync(token);
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
