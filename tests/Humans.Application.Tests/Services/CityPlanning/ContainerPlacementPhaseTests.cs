using AwesomeAssertions;
using Humans.Application.Configuration;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.CityPlanning;
using Humans.Application.Tests.Infrastructure;
using Humans.Infrastructure.Repositories.CityPlanning;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.CityPlanning;

public sealed class ContainerPlacementPhaseTests : ServiceTestHarness
{
    private readonly ICampServiceRead _campService;
    private readonly CityPlanningService _sut;

    public ContainerPlacementPhaseTests()
        : base(Instant.FromUtc(2026, 4, 26, 10, 0))
    {
        _campService = Substitute.For<ICampServiceRead>();
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(PublicYear: 2026, OpenSeasons: [], EeStartDate: null));
        var repo = new CityPlanningRepository(DbFactory);
        var options = new CityPlanningOptions { CityPlanningTeamSlug = "city-planning" };
        _sut = new CityPlanningService(
            repo, Clock, Options.Create(options),
            _campService,
            Substitute.For<ITeamService>(),
            Substitute.For<IUserService>());
    }

    [HumansFact]
    public async Task OpenContainerPlacement_SetsIsOpenAndTimestamp()
    {
        var userId = Guid.NewGuid();

        await _sut.OpenContainerPlacementAsync(userId);

        var settings = await _sut.GetSettingsAsync();
        settings.IsContainerPlacementOpen.Should().BeTrue();
        settings.ContainerPlacementOpenedAt.Should().Be(Clock.GetCurrentInstant());
        settings.ContainerPlacementClosedAt.Should().BeNull();
    }

    [HumansFact]
    public async Task CloseContainerPlacement_SetsIsClosedAndTimestamp()
    {
        var userId = Guid.NewGuid();
        await _sut.OpenContainerPlacementAsync(userId);

        await _sut.CloseContainerPlacementAsync(userId);

        var settings = await _sut.GetSettingsAsync();
        settings.IsContainerPlacementOpen.Should().BeFalse();
        settings.ContainerPlacementClosedAt.Should().Be(Clock.GetCurrentInstant());
    }
}
