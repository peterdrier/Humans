using Humans.Containers.Contracts;
using AwesomeAssertions;
using Humans.CityPlanning.Contracts;
using Humans.Audit.Contracts;
using Humans.Camps.Contracts;
using Humans.Teams.Contracts;
using Humans.CityPlanning.Services;

using Humans.CityPlanning.Data;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Humans.Users.Contracts;

namespace Humans.CityPlanning.Tests;

public sealed class ContainerPlacementPhaseTests : CityPlanningTestBase
{
    private readonly CityPlanningService _sut;

    public ContainerPlacementPhaseTests()
        : base(Instant.FromUtc(2026, 4, 26, 10, 0))
    {
        var campService = Substitute.For<ICampService>();
        campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(PublicYear: 2026, OpenSeasons: []));
        var repo = new CityPlanningRepository(CityPlanningDbFactory);
        var options = new CityPlanningOptions { CityPlanningTeamSlug = "city-planning" };
        _sut = new CityPlanningService(
            repo, Clock, Options.Create(options),
            campService,
            Substitute.For<ITeamService>(),
            Substitute.For<IUserService>(),
            Substitute.For<IAuditLogService>(),
            ActiveEvent(2026), Substitute.For<IContainerService>());
    }

    [HumansFact]
    public async Task ContainerPlacement_Open_SetsOpenTimestamp()
    {
        var userId = Guid.NewGuid();

        await _sut.OpenContainerPlacementAsync(userId, Xunit.TestContext.Current.CancellationToken);

        var settings = (await _sut.GetSettingsAsync(Xunit.TestContext.Current.CancellationToken))!;
        settings.IsContainerPlacementOpen.Should().BeTrue();
        settings.ContainerPlacementOpenedAt.Should().Be(Clock.GetCurrentInstant());
        settings.ContainerPlacementClosedAt.Should().BeNull();
    }

    [HumansFact]
    public async Task ContainerPlacement_Close_SetsClosedTimestamp()
    {
        var userId = Guid.NewGuid();
        await _sut.OpenContainerPlacementAsync(userId, Xunit.TestContext.Current.CancellationToken);

        await _sut.CloseContainerPlacementAsync(userId, Xunit.TestContext.Current.CancellationToken);

        var settings = (await _sut.GetSettingsAsync(Xunit.TestContext.Current.CancellationToken))!;
        settings.IsContainerPlacementOpen.Should().BeFalse();
        settings.ContainerPlacementClosedAt.Should().Be(Clock.GetCurrentInstant());
    }
}
