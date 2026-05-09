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

    private async Task<Container> SeedContainerAsync(
        string? imagePath = null,
        string? placementImagePath = null,
        string? placementNotes = null)
    {
        await using var ctx = new HumansDbContext(_dbOptions);
        var container = new Container
        {
            Id = Guid.NewGuid(),
            Year = 2026,
            Name = "Container A",
            ImageStoragePath = imagePath,
            ImageContentType = imagePath is not null ? "image/jpeg" : null,
            ImageFileName = imagePath is not null ? "main.jpg" : null,
            PlacementImageStoragePath = placementImagePath,
            PlacementImageContentType = placementImagePath is not null ? "image/jpeg" : null,
            PlacementImageFileName = placementImagePath is not null ? "placement.jpg" : null,
            PlacementNotes = placementNotes,
            CreatedAt = _startTime,
            UpdatedAt = _startTime,
        };
        ctx.Containers.Add(container);
        await ctx.SaveChangesAsync();
        return container;
    }

    [HumansFact]
    public async Task CreateAsync_WithBothImages_SavesBothImages()
    {
        _imageStorage.SaveImageAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), "image/jpeg", ContainerImageKind.Main, Arg.Any<CancellationToken>())
            .Returns("uploads/containers/id/main-guid.jpg");
        _imageStorage.SaveImageAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), "image/jpeg", ContainerImageKind.Placement, Arg.Any<CancellationToken>())
            .Returns("uploads/containers/id/placement-guid.jpg");

        var result = await _sut.CreateAsync(new ContainerData(
            CampId: null,
            Year: 2026,
            Name: "Test",
            Description: null,
            PlacementNotes: "Near the gate",
            MainImage: FakeImage("main"),
            PlacementImage: FakeImage("placement")));

        result.ImageStoragePath.Should().Be("/uploads/containers/id/main-guid.jpg");
        result.PlacementImageStoragePath.Should().Be("/uploads/containers/id/placement-guid.jpg");
        result.PlacementNotes.Should().Be("Near the gate");
    }

    [HumansFact]
    public async Task UpdateAsync_RemoveMainImage_DeletesMainImageOnly()
    {
        var container = await SeedContainerAsync(
            imagePath: "uploads/containers/id/main-guid.jpg",
            placementImagePath: "uploads/containers/id/placement-guid.jpg");

        await _sut.UpdateAsync(container.Id, new ContainerData(
            CampId: null,
            Year: container.Year,
            Name: container.Name,
            Description: null,
            RemoveMainImage: true));

        _imageStorage.Received(1).DeleteImage("uploads/containers/id/main-guid.jpg");
        _imageStorage.DidNotReceive().DeleteImage("uploads/containers/id/placement-guid.jpg");

        var updated = await _sut.GetByIdAsync(container.Id);
        updated!.ImageStoragePath.Should().BeNull();
        updated.PlacementImageStoragePath.Should().Be("/uploads/containers/id/placement-guid.jpg");
    }

    [HumansFact]
    public async Task UpdateAsync_RemovePlacementImage_DeletesPlacementImageOnly()
    {
        var container = await SeedContainerAsync(
            imagePath: "uploads/containers/id/main-guid.jpg",
            placementImagePath: "uploads/containers/id/placement-guid.jpg");

        await _sut.UpdateAsync(container.Id, new ContainerData(
            CampId: null,
            Year: container.Year,
            Name: container.Name,
            Description: null,
            RemovePlacementImage: true));

        _imageStorage.Received(1).DeleteImage("uploads/containers/id/placement-guid.jpg");
        _imageStorage.DidNotReceive().DeleteImage("uploads/containers/id/main-guid.jpg");

        var updated = await _sut.GetByIdAsync(container.Id);
        updated!.PlacementImageStoragePath.Should().BeNull();
        updated.ImageStoragePath.Should().Be("/uploads/containers/id/main-guid.jpg");
    }

    [HumansFact]
    public async Task UpdateAsync_ReplaceMainImage_DeletesPriorMainAndSavesNew()
    {
        var container = await SeedContainerAsync(imagePath: "uploads/containers/id/main-old.jpg");
        _imageStorage.SaveImageAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), "image/jpeg", ContainerImageKind.Main, Arg.Any<CancellationToken>())
            .Returns("uploads/containers/id/main-new.jpg");

        await _sut.UpdateAsync(container.Id, new ContainerData(
            CampId: null,
            Year: container.Year,
            Name: container.Name,
            Description: null,
            MainImage: FakeImage("main")));

        _imageStorage.Received(1).DeleteImage("uploads/containers/id/main-old.jpg");

        var updated = await _sut.GetByIdAsync(container.Id);
        updated!.ImageStoragePath.Should().Be("/uploads/containers/id/main-new.jpg");
    }

    [HumansFact]
    public async Task UpdateAsync_RemoveBothImages_DeletesBoth()
    {
        var container = await SeedContainerAsync(
            imagePath: "uploads/containers/id/main.jpg",
            placementImagePath: "uploads/containers/id/placement.jpg");

        await _sut.UpdateAsync(container.Id, new ContainerData(
            CampId: null,
            Year: container.Year,
            Name: container.Name,
            Description: null,
            RemoveMainImage: true,
            RemovePlacementImage: true));

        _imageStorage.Received(1).DeleteImage("uploads/containers/id/main.jpg");
        _imageStorage.Received(1).DeleteImage("uploads/containers/id/placement.jpg");

        var updated = await _sut.GetByIdAsync(container.Id);
        updated!.ImageStoragePath.Should().BeNull();
        updated.PlacementImageStoragePath.Should().BeNull();
    }

    [HumansFact]
    public async Task UpdateAsync_ClearPlacementNotes_SetsToNull()
    {
        var container = await SeedContainerAsync(placementNotes: "Old notes");

        await _sut.UpdateAsync(container.Id, new ContainerData(
            CampId: null,
            Year: container.Year,
            Name: container.Name,
            Description: null,
            PlacementNotes: null));

        var updated = await _sut.GetByIdAsync(container.Id);
        updated!.PlacementNotes.Should().BeNull();
    }

    [HumansFact]
    public async Task DeleteAsync_RemovesBothImages()
    {
        var container = await SeedContainerAsync(
            imagePath: "uploads/containers/id/main.jpg",
            placementImagePath: "uploads/containers/id/placement.jpg");

        await _sut.DeleteAsync(container.Id);

        _imageStorage.Received(1).DeleteImage("uploads/containers/id/main.jpg");
        _imageStorage.Received(1).DeleteImage("uploads/containers/id/placement.jpg");
    }
}
