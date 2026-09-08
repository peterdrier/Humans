using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Camps.Contracts;
using Humans.Containers.Contracts;
using Humans.Containers.Data;
using Humans.Containers.Domain;
using Humans.Containers.Services;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Containers.Tests.Services;

public sealed class ServicePlacementTests
{
    private const int Year = 2026;
    private const string GeoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]},"properties":{"center_lng":-0.137,"center_lat":41.699,"rotation_degrees":0}}""";
    private static readonly Guid CampId = Guid.Parse("00000000-0000-0000-0099-000000000002");
    private static readonly Guid ActorUserId = Guid.Parse("00000000-0000-0000-0099-000000000003");

    private readonly IFileStorage _fileStorage = Substitute.For<IFileStorage>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();
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
            _fileStorage,
            Substitute.For<ICampServiceRead>(),
            _auditLog,
            Clock);
    }

    private static ContainerImageUpload Sketch(string name = "sketch.jpg") =>
        new(Stream.Null, "image/jpeg", name, 1024);

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

    private async Task<Container> SeedPlacedContainerAsync()
    {
        var container = await SeedContainerAsync();
        await _sut.SavePlacementAsync(container.Id, Year, GeoJson, ActorUserId, Xunit.TestContext.Current.CancellationToken);
        return container;
    }

    private async Task<ContainerPlacementDto> GetPlacementAsync(Guid containerId) =>
        (await _sut.GetPlacementsByYearAsync(Year, Xunit.TestContext.Current.CancellationToken))
            .Single(p => p.ContainerId == containerId);

    [HumansFact]
    public async Task SavePlacementAsync_SetsLocationGeoJsonAndUpdatedAt()
    {
        var container = await SeedContainerAsync();
        Clock.AdvanceSeconds(60);

        var result = await _sut.SavePlacementAsync(container.Id, Year, GeoJson, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        result.LocationGeoJson.Should().Be(GeoJson);
        result.UpdatedAt.Should().Be(Clock.GetCurrentInstant());
        result.Year.Should().Be(Year);
    }

    [HumansFact]
    public async Task SavePlacementAsync_AuditsUnderThePlacementEntityType()
    {
        var container = await SeedContainerAsync();

        await _sut.SavePlacementAsync(container.Id, Year, GeoJson, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        await _auditLog.Received(1).LogAsync(
            AuditAction.ContainerPlacementSaved,
            AuditEntityTypes.ContainerPlacement,
            container.Id,
            Arg.Any<string>(),
            ActorUserId,
            container.Id,
            AuditEntityTypes.Container);
    }

    [HumansFact]
    public async Task SavePlacementAsync_ThrowsWhenContainerNotFound()
    {
        var act = async () => await _sut.SavePlacementAsync(Guid.NewGuid(), Year, "{}", ActorUserId, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Container not found.");
    }

    [HumansFact]
    public async Task SavePlacementAsync_PreservesNotesAndImageOnAnExistingRow()
    {
        var container = await SeedPlacedContainerAsync();
        await _sut.UpdatePlacementNotesAsync(container.Id, Year, "by the gate", Sketch(), removeImage: false, ActorUserId, Xunit.TestContext.Current.CancellationToken);
        var moved = GeoJson.Replace("-0.137", "-0.138", StringComparison.Ordinal);

        var result = await _sut.SavePlacementAsync(container.Id, Year, moved, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        result.LocationGeoJson.Should().Be(moved);
        result.PlacementNotes.Should().Be("by the gate");
        result.PlacementImageUrl.Should().StartWith($"/uploads/containers/{container.Id}/");
        result.PlacementImageFileName.Should().Be("sketch.jpg");
    }

    [HumansFact]
    public async Task UpdatePlacementNotesAsync_RequiresAnExistingPlacementRow()
    {
        var container = await SeedContainerAsync();

        var act = async () => await _sut.UpdatePlacementNotesAsync(container.Id, Year, "notes", image: null, removeImage: false, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Placement not found.*");
        await _auditLog.DidNotReceive().LogAsync(
            AuditAction.ContainerPlacementNotesUpdated, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task UpdatePlacementNotesAsync_AddsTheImageUnderTheContainersPrefix()
    {
        var container = await SeedPlacedContainerAsync();

        var result = await _sut.UpdatePlacementNotesAsync(container.Id, Year, null, Sketch(), removeImage: false, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        result.PlacementImageUrl.Should().StartWith($"/uploads/containers/{container.Id}/").And.EndWith(".jpg");
        result.PlacementImageFileName.Should().Be("sketch.jpg");
        await _fileStorage.Received(1).SaveAsync(
            Arg.Is<string>(k => k.StartsWith($"uploads/containers/{container.Id}/")), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _fileStorage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdatePlacementNotesAsync_ReplacingTheImageDeletesTheOldFile()
    {
        var container = await SeedPlacedContainerAsync();
        var first = await _sut.UpdatePlacementNotesAsync(container.Id, Year, null, Sketch("first.jpg"), removeImage: false, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        var second = await _sut.UpdatePlacementNotesAsync(container.Id, Year, null, Sketch("second.jpg"), removeImage: false, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        second.PlacementImageUrl.Should().NotBe(first.PlacementImageUrl);
        second.PlacementImageFileName.Should().Be("second.jpg");
        await _fileStorage.Received(1).DeleteAsync(first.PlacementImageUrl!.TrimStart('/'), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdatePlacementNotesAsync_RemoveImageDeletesTheFileAndClearsTheColumns()
    {
        var container = await SeedPlacedContainerAsync();
        var withImage = await _sut.UpdatePlacementNotesAsync(container.Id, Year, "keep me", Sketch(), removeImage: false, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        var result = await _sut.UpdatePlacementNotesAsync(container.Id, Year, "keep me", image: null, removeImage: true, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        result.PlacementImageUrl.Should().BeNull();
        result.PlacementImageFileName.Should().BeNull();
        result.PlacementNotes.Should().Be("keep me");
        await _fileStorage.Received(1).DeleteAsync(withImage.PlacementImageUrl!.TrimStart('/'), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdatePlacementNotesAsync_RejectsAnImageOver10MB()
    {
        var container = await SeedPlacedContainerAsync();
        var tooBig = new ContainerImageUpload(Stream.Null, "image/jpeg", "big.jpg", 10 * 1024 * 1024 + 1);

        var act = async () => await _sut.UpdatePlacementNotesAsync(container.Id, Year, null, tooBig, removeImage: false, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*under 10 MB*");
        await _fileStorage.DidNotReceive().SaveAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ClearPlacementAsync_DeletesRow()
    {
        var container = await SeedPlacedContainerAsync();

        await _sut.ClearPlacementAsync(container.Id, Year, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        var placements = await _sut.GetPlacementsByYearAsync(Year, Xunit.TestContext.Current.CancellationToken);
        placements.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ClearPlacementAsync_PreservesRowWhenNotesPresent()
    {
        var container = await SeedPlacedContainerAsync();
        await _sut.UpdatePlacementNotesAsync(container.Id, Year, "under the shade tree", image: null, removeImage: false, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        await _sut.ClearPlacementAsync(container.Id, Year, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        var placement = await GetPlacementAsync(container.Id);
        placement.LocationGeoJson.Should().BeNull();
        placement.PlacementNotes.Should().Be("under the shade tree");
    }

    [HumansFact]
    public async Task ClearPlacementAsync_PreservesRowWhenOnlyAnImageIsPresent()
    {
        var container = await SeedPlacedContainerAsync();
        await _sut.UpdatePlacementNotesAsync(container.Id, Year, null, Sketch(), removeImage: false, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        await _sut.ClearPlacementAsync(container.Id, Year, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        var placement = await GetPlacementAsync(container.Id);
        placement.IsPlaced.Should().BeFalse();
        placement.HasPlacementInfo.Should().BeTrue();
        placement.PlacementImageUrl.Should().NotBeNull();
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
        var container = await SeedPlacedContainerAsync();
        await _sut.SavePlacementAsync(container.Id, Year + 1, GeoJson, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        await _sut.DeleteAsync(container.Id, ActorUserId, Xunit.TestContext.Current.CancellationToken);

        (await _sut.GetPlacementsByYearAsync(Year, Xunit.TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await _sut.GetPlacementsByYearAsync(Year + 1, Xunit.TestContext.Current.CancellationToken)).Should().BeEmpty();
    }
}
