using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Camps.Contracts;
using Humans.Containers.Data;
using Humans.Containers.Domain;
using Humans.Containers.Services;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Containers.Tests;

public sealed class ServicePlacementTests
{
    private const int Year = 2026;
    private static readonly Guid CampId = Guid.Parse("00000000-0000-0000-0099-000000000002");
    private static readonly Guid ActorUserId = Guid.Parse("00000000-0000-0000-0099-000000000003");

    private readonly Service _sut;
    private readonly FakeClock Clock = new(Instant.FromUtc(2026, 4, 26, 10, 0));

    private readonly DbContextOptions<ContainersDbContext> _containersOptions =
        new DbContextOptionsBuilder<ContainersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    public ServicePlacementTests()
    {
        var repo = new Repository(new TestDbContextFactory<ContainersDbContext>(_containersOptions));
        _sut = new Service(
            repo,
            Substitute.For<IFileStorage>(),
            Substitute.For<ICampServiceRead>(),
            Substitute.For<IAuditLogService>(),
            Clock);
    }

    private async Task<Container> SeedContainerAsync()
    {
        var now = Clock.GetCurrentInstant();
        var container = new Container
        {
            Id = Guid.NewGuid(),
            CampId = CampId,
            Name = "Container A",
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var ctx = new ContainersDbContext(_containersOptions);
        ctx.Containers.Add(container);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        return container;
    }

    [HumansFact]
    public async Task SavePlacementAsync_SetsLocationGeoJsonAndUpdatedAt()
    {
        var container = await SeedContainerAsync();
        var geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]},"properties":{"center_lng":-0.137,"center_lat":41.699,"rotation_degrees":0}}""";
        Clock.AdvanceSeconds(60);

        var result = await _sut.SavePlacementAsync(container.Id, Year, geoJson, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        result.LocationGeoJson.Should().Be(geoJson);
        result.UpdatedAt.Should().Be(Clock.GetCurrentInstant());
        result.Year.Should().Be(Year);
    }

    [HumansFact]
    public async Task SavePlacementAsync_ThrowsWhenContainerNotFound()
    {
        var act = async () => await _sut.SavePlacementAsync(Guid.NewGuid(), Year, "{}", ActorUserId, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Container not found.");
    }

    [HumansFact]
    public async Task ClearPlacementAsync_DeletesRow()
    {
        var container = await SeedContainerAsync();
        var geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]},"properties":{"center_lng":0,"center_lat":0,"rotation_degrees":0}}""";
        await _sut.SavePlacementAsync(container.Id, Year, geoJson, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        await _sut.ClearPlacementAsync(container.Id, Year, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        var placements = await _sut.GetPlacementsByYearAsync(Year, Xunit.TestContext.Current.CancellationToken);
        placements.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ClearPlacementAsync_PreservesRowWhenNotesPresent()
    {
        var container = await SeedContainerAsync();
        var geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]},"properties":{"center_lng":0,"center_lat":0,"rotation_degrees":0}}""";
        await _sut.SavePlacementAsync(container.Id, Year, geoJson, ActorUserId, Xunit.TestContext.Current.CancellationToken);
        await _sut.UpdatePlacementNotesAsync(container.Id, Year, "under the shade tree", image: null, removeImage: false, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        await _sut.ClearPlacementAsync(container.Id, Year, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        var placement = (await _sut.GetPlacementsByYearAsync(Year, Xunit.TestContext.Current.CancellationToken)).Single();
        placement.LocationGeoJson.Should().BeNull();
        placement.PlacementNotes.Should().Be("under the shade tree");
    }

    [HumansFact]
    public async Task ClearPlacementAsync_NoOpWhenContainerHasNoPlacement()
    {
        var container = await SeedContainerAsync();

        var act = async () => await _sut.ClearPlacementAsync(container.Id, Year, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [HumansFact]
    public async Task DeleteAsync_AlsoRemovesAssociatedPlacements()
    {
        var container = await SeedContainerAsync();
        var geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]},"properties":{"center_lng":0,"center_lat":0,"rotation_degrees":0}}""";
        await _sut.SavePlacementAsync(container.Id, Year, geoJson, ActorUserId, Xunit.TestContext.Current.CancellationToken);
        await _sut.SavePlacementAsync(container.Id, Year + 1, geoJson, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        await _sut.DeleteAsync(container.Id, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        (await _sut.GetPlacementsByYearAsync(Year, Xunit.TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await _sut.GetPlacementsByYearAsync(Year + 1, Xunit.TestContext.Current.CancellationToken)).Should().BeEmpty();
    }
}
