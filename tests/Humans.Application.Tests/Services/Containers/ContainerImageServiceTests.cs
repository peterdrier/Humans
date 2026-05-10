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

public class ContainerImageServiceTests : IDisposable
{
    private readonly DbContextOptions<HumansDbContext> _dbOptions;
    private readonly FakeClock _clock;
    private readonly IContainerImageStorage _imageStorage;
    private readonly ContainerService _sut;
    private readonly Instant _startTime = Instant.FromUtc(2026, 5, 8, 10, 0, 0);
    private static readonly Guid CampId = Guid.Parse("00000000-0000-0000-0099-000000000001");

    public ContainerImageServiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _clock = new FakeClock(_startTime);
        _imageStorage = Substitute.For<IContainerImageStorage>();
        var repo = new ContainerRepository(new TestDbContextFactory(_dbOptions));
        _sut = new ContainerService(
            repo,
            _imageStorage,
            Substitute.For<ICampService>(),
            Substitute.For<ICityPlanningService>(),
            _clock);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private static ContainerImageUpload FakeImage(string kind = "main") =>
        new(Stream.Null, "image/jpeg", $"{kind}-sketch.jpg", 1024);

    private async Task<Container> SeedContainerAsync(string? imagePath = null)
    {
        await using var ctx = new HumansDbContext(_dbOptions);
        var container = new Container
        {
            Id = Guid.NewGuid(),
            CampId = CampId,
            Name = "Container A",
            ImageStoragePath = imagePath,
            ImageContentType = imagePath is not null ? "image/jpeg" : null,
            ImageFileName = imagePath is not null ? "main.jpg" : null,
            CreatedAt = _startTime,
            UpdatedAt = _startTime,
        };
        ctx.Containers.Add(container);
        await ctx.SaveChangesAsync();
        return container;
    }

    [HumansFact]
    public async Task CreateAsync_WithMainImage_SavesMainImage()
    {
        _imageStorage.SaveImageAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), "image/jpeg", ContainerImageKind.Main, Arg.Any<CancellationToken>())
            .Returns("uploads/containers/id/main-guid.jpg");

        var result = await _sut.CreateAsync(new ContainerData(
            CampId: CampId,
            Name: "Test",
            Description: null,
            MainImage: FakeImage("main")));

        result.ImageStoragePath.Should().Be("/uploads/containers/id/main-guid.jpg");
        result.CampId.Should().Be(CampId);
    }

    [HumansFact]
    public async Task UpdateAsync_RemoveMainImage_DeletesMainImage()
    {
        var container = await SeedContainerAsync(imagePath: "uploads/containers/id/main-guid.jpg");

        await _sut.UpdateAsync(container.Id, new ContainerData(
            CampId: container.CampId,
            Name: container.Name,
            Description: null,
            RemoveMainImage: true));

        _imageStorage.Received(1).DeleteImage("uploads/containers/id/main-guid.jpg");

        var updated = await _sut.GetByIdAsync(container.Id);
        updated!.ImageStoragePath.Should().BeNull();
    }

    [HumansFact]
    public async Task UpdateAsync_ReplaceMainImage_DeletesPriorMainAndSavesNew()
    {
        var container = await SeedContainerAsync(imagePath: "uploads/containers/id/main-old.jpg");
        _imageStorage.SaveImageAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), "image/jpeg", ContainerImageKind.Main, Arg.Any<CancellationToken>())
            .Returns("uploads/containers/id/main-new.jpg");

        await _sut.UpdateAsync(container.Id, new ContainerData(
            CampId: container.CampId,
            Name: container.Name,
            Description: null,
            MainImage: FakeImage("main")));

        _imageStorage.Received(1).DeleteImage("uploads/containers/id/main-old.jpg");

        var updated = await _sut.GetByIdAsync(container.Id);
        updated!.ImageStoragePath.Should().Be("/uploads/containers/id/main-new.jpg");
    }

    [HumansFact]
    public async Task DeleteAsync_RemovesMainImage()
    {
        var container = await SeedContainerAsync(imagePath: "uploads/containers/id/main.jpg");

        await _sut.DeleteAsync(container.Id);

        _imageStorage.Received(1).DeleteImage("uploads/containers/id/main.jpg");
    }

    [HumansFact]
    public async Task UpsertPlacementMetadataAsync_WithImage_SavesPlacementImage()
    {
        var container = await SeedContainerAsync();
        _imageStorage.SaveImageAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), "image/jpeg", ContainerImageKind.Placement, Arg.Any<CancellationToken>())
            .Returns("uploads/containers/id/placement-guid.jpg");

        var result = await _sut.UpsertPlacementMetadataAsync(new ContainerPlacementData(
            ContainerId: container.Id,
            Year: 2026,
            LocationGeoJson: null,
            PlacementNotes: "Near the gate",
            PlacementImage: FakeImage("placement")));

        result.PlacementImageStoragePath.Should().Be("/uploads/containers/id/placement-guid.jpg");
        result.PlacementNotes.Should().Be("Near the gate");
    }

    [HumansFact]
    public async Task UpsertPlacementMetadataAsync_RemovePlacementImage_DeletesImage()
    {
        var container = await SeedContainerAsync();
        // Seed a placement with an image
        _imageStorage.SaveImageAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), "image/jpeg", ContainerImageKind.Placement, Arg.Any<CancellationToken>())
            .Returns("uploads/containers/id/placement-guid.jpg");
        await _sut.UpsertPlacementMetadataAsync(new ContainerPlacementData(
            ContainerId: container.Id,
            Year: 2026,
            LocationGeoJson: null,
            PlacementNotes: null,
            PlacementImage: FakeImage("placement")));

        await _sut.UpsertPlacementMetadataAsync(new ContainerPlacementData(
            ContainerId: container.Id,
            Year: 2026,
            LocationGeoJson: null,
            PlacementNotes: null,
            RemovePlacementImage: true));

        _imageStorage.Received(1).DeleteImage("uploads/containers/id/placement-guid.jpg");

        var placement = await _sut.GetPlacementAsync(container.Id, 2026);
        placement!.PlacementImageStoragePath.Should().BeNull();
    }
}
