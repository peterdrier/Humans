using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Services.Containers;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Infrastructure.Repositories.Containers;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Containers;

public sealed class ContainerPlacementServiceTests : ServiceTestHarness
{
    private const int Year = 2026;
    private static readonly Guid CampId = Guid.Parse("00000000-0000-0000-0099-000000000002");
    private static readonly Guid ActorUserId = Guid.Parse("00000000-0000-0000-0099-000000000003");

    private readonly ContainerService _sut;

    public ContainerPlacementServiceTests()
        : base(Instant.FromUtc(2026, 4, 26, 10, 0))
    {
        var repo = new ContainerRepository(DbFactory);
        _sut = new ContainerService(
            repo,
            Substitute.For<IFileStorage>(),
            Substitute.For<ICampService>(),
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
        Db.Containers.Add(container);
        await Db.SaveChangesAsync();
        return container;
    }

    [HumansFact]
    public async Task SavePlacementAsync_SetsLocationGeoJsonAndUpdatedAt()
    {
        var container = await SeedContainerAsync();
        var geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]},"properties":{"center_lng":-0.137,"center_lat":41.699,"rotation_degrees":0}}""";
        Clock.AdvanceSeconds(60);

        var result = await _sut.SavePlacementAsync(container.Id, Year, geoJson, ActorUserId);

        result.LocationGeoJson.Should().Be(geoJson);
        result.UpdatedAt.Should().Be(Clock.GetCurrentInstant());
        result.Year.Should().Be(Year);
    }

    [HumansFact]
    public async Task SavePlacementAsync_ThrowsWhenContainerNotFound()
    {
        var act = async () => await _sut.SavePlacementAsync(Guid.NewGuid(), Year, "{}", ActorUserId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Container not found.");
    }

    [HumansFact]
    public async Task ClearPlacementAsync_DeletesRow()
    {
        var container = await SeedContainerAsync();
        var geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]},"properties":{"center_lng":0,"center_lat":0,"rotation_degrees":0}}""";
        await _sut.SavePlacementAsync(container.Id, Year, geoJson, ActorUserId);

        await _sut.ClearPlacementAsync(container.Id, Year, ActorUserId);

        var placement = await _sut.GetPlacementAsync(container.Id, Year);
        placement.Should().BeNull();
    }

    [HumansFact]
    public async Task ClearPlacementAsync_NoOpWhenContainerHasNoPlacement()
    {
        var container = await SeedContainerAsync();

        var act = async () => await _sut.ClearPlacementAsync(container.Id, Year, ActorUserId);

        await act.Should().NotThrowAsync();
    }

    [HumansFact]
    public async Task DeleteAsync_AlsoRemovesAssociatedPlacements()
    {
        var container = await SeedContainerAsync();
        var geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]},"properties":{"center_lng":0,"center_lat":0,"rotation_degrees":0}}""";
        await _sut.SavePlacementAsync(container.Id, Year, geoJson, ActorUserId);
        await _sut.SavePlacementAsync(container.Id, Year + 1, geoJson, ActorUserId);

        await _sut.DeleteAsync(container.Id, ActorUserId);

        (await _sut.GetPlacementAsync(container.Id, Year)).Should().BeNull();
        (await _sut.GetPlacementAsync(container.Id, Year + 1)).Should().BeNull();
    }
}
