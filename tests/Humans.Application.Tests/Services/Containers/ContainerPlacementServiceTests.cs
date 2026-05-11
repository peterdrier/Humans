using AwesomeAssertions;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CitiPlanning;
using Humans.Application.Interfaces.Containers;
using Humans.Application.Services.Containers;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Containers;
using Humans.Testing;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services.Containers;

public class ContainerPlacementServiceTests : IDisposable
{
    private const int Year = 2026;
    private static readonly Guid CampId = Guid.Parse("00000000-0000-0000-0099-000000000002");

    private readonly DbContextOptions<HumansDbContext> _dbOptions;
    private readonly FakeClock _clock;
    private readonly ContainerService _sut;
    private readonly Instant _startTime = Instant.FromUtc(2026, 4, 26, 10, 0, 0);

    public ContainerPlacementServiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _clock = new FakeClock(_startTime);
        var repo = new ContainerRepository(new TestDbContextFactory(_dbOptions));
        _sut = new ContainerService(
            repo,
            Substitute.For<IContainerImageStorage>(),
            Substitute.For<ICampService>(),
            Substitute.For<ICityPlanningService>(),
            _clock);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private async Task<Container> SeedContainerAsync()
    {
        await using var ctx = new HumansDbContext(_dbOptions);
        var container = new Container
        {
            Id = Guid.NewGuid(),
            CampId = CampId,
            Name = "Container A",
            CreatedAt = _startTime,
            UpdatedAt = _startTime,
        };
        ctx.Containers.Add(container);
        await ctx.SaveChangesAsync();
        return container;
    }

    [HumansFact]
    public async Task SavePlacementAsync_SetsLocationGeoJsonAndUpdatedAt()
    {
        var container = await SeedContainerAsync();
        var geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]},"properties":{"center_lng":-0.137,"center_lat":41.699,"rotation_degrees":0}}""";
        _clock.AdvanceSeconds(60);

        var result = await _sut.SavePlacementAsync(container.Id, Year, geoJson);

        result.LocationGeoJson.Should().Be(geoJson);
        result.UpdatedAt.Should().Be(_clock.GetCurrentInstant());
        result.Year.Should().Be(Year);
    }

    [HumansFact]
    public async Task SavePlacementAsync_ThrowsWhenContainerNotFound()
    {
        var act = async () => await _sut.SavePlacementAsync(Guid.NewGuid(), Year, "{}");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Container not found.");
    }

    [HumansFact]
    public async Task ClearPlacementAsync_DeletesRow()
    {
        var container = await SeedContainerAsync();
        var geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]},"properties":{"center_lng":0,"center_lat":0,"rotation_degrees":0}}""";
        await _sut.SavePlacementAsync(container.Id, Year, geoJson);

        await _sut.ClearPlacementAsync(container.Id, Year);

        var placement = await _sut.GetPlacementAsync(container.Id, Year);
        placement.Should().BeNull();
    }

    [HumansFact]
    public async Task ClearPlacementAsync_NoOpWhenContainerHasNoPlacement()
    {
        var container = await SeedContainerAsync();

        var act = async () => await _sut.ClearPlacementAsync(container.Id, Year);

        await act.Should().NotThrowAsync();
    }

    [HumansFact]
    public async Task DeleteAsync_AlsoRemovesAssociatedPlacements()
    {
        var container = await SeedContainerAsync();
        var geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]},"properties":{"center_lng":0,"center_lat":0,"rotation_degrees":0}}""";
        await _sut.SavePlacementAsync(container.Id, Year, geoJson);
        await _sut.SavePlacementAsync(container.Id, Year + 1, geoJson);

        await _sut.DeleteAsync(container.Id);

        (await _sut.GetPlacementAsync(container.Id, Year)).Should().BeNull();
        (await _sut.GetPlacementAsync(container.Id, Year + 1)).Should().BeNull();
    }
}
