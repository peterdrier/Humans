using AwesomeAssertions;
using Humans.Application.Interfaces.SystemSettings;
using Humans.Infrastructure.Repositories.GoogleIntegration;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Repositories;

public sealed class DriveActivityMonitorRepositoryTests
{
    private readonly ISystemSettingsService _systemSettings = Substitute.For<ISystemSettingsService>();
    private readonly DriveActivityMonitorRepository _repository;

    public DriveActivityMonitorRepositoryTests()
    {
        _repository = new DriveActivityMonitorRepository(
            _systemSettings,
            NullLogger<DriveActivityMonitorRepository>.Instance);
    }

    [HumansFact]
    public async Task GetLastRunTimestampAsync_ReturnsNull_WhenNoValueExists()
    {
        var result = await _repository.GetLastRunTimestampAsync();

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetLastRunTimestampAsync_ReturnsNull_WhenValueIsEmpty()
    {
        _systemSettings.GetValueAsync(DriveActivityMonitorRepository.LastRunSettingKey, Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        var result = await _repository.GetLastRunTimestampAsync();

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetLastRunTimestampAsync_ReturnsNull_WhenValueIsUnparsable()
    {
        _systemSettings.GetValueAsync(DriveActivityMonitorRepository.LastRunSettingKey, Arg.Any<CancellationToken>())
            .Returns("not-an-instant");

        var result = await _repository.GetLastRunTimestampAsync();

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetLastRunTimestampAsync_RoundTripsStoredValue()
    {
        var expected = Instant.FromUtc(2026, 4, 22, 10, 15, 30);
        _systemSettings.GetValueAsync(DriveActivityMonitorRepository.LastRunSettingKey, Arg.Any<CancellationToken>())
            .Returns(NodaTime.Text.InstantPattern.General.Format(expected));

        var result = await _repository.GetLastRunTimestampAsync();

        result.Should().Be(expected);
    }

    [HumansFact]
    public async Task AdvanceLastRunMarkerAsync_AdvancesMarker()
    {
        var marker = Instant.FromUtc(2026, 4, 22, 11, 0);

        await _repository.AdvanceLastRunMarkerAsync(marker);

        await _systemSettings.Received(1).SetValueAsync(
            DriveActivityMonitorRepository.LastRunSettingKey,
            NodaTime.Text.InstantPattern.General.Format(marker),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AdvanceLastRunMarkerAsync_WithNullMarker_IsNoOp()
    {
        await _repository.AdvanceLastRunMarkerAsync(newLastRunAt: null);

        await _systemSettings.DidNotReceiveWithAnyArgs().SetValueAsync(null!, null!, CancellationToken.None);
    }
}
