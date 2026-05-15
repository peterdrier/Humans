using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Containers;
using Humans.Application.Services.Containers;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Containers;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services.Containers;

public class ContainerImageServiceTests : IDisposable
{
    private readonly DbContextOptions<HumansDbContext> _dbOptions;
    private readonly FakeClock _clock;
    private readonly IFileStorage _fileStorage;
    private readonly ContainerService _sut;
    private readonly Instant _startTime = Instant.FromUtc(2026, 5, 8, 10, 0, 0);
    private static readonly Guid CampId = Guid.Parse("00000000-0000-0000-0099-000000000001");

    public ContainerImageServiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _clock = new FakeClock(_startTime);
        _fileStorage = Substitute.For<IFileStorage>();
        var repo = new ContainerRepository(new TestDbContextFactory(_dbOptions));
        _sut = new ContainerService(
            repo,
            _fileStorage,
            Substitute.For<ICampService>(),
            Substitute.For<IAuditLogService>(),
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
    public async Task CreateAsync_WithMainImage_SavesUnderContainersPrefix()
    {
        var result = await _sut.CreateAsync(actorUserId: Guid.NewGuid(), data: new ContainerData(
            CampId: CampId,
            Name: "Test",
            Description: null,
            MainImage: FakeImage("main")));

        result.CampId.Should().Be(CampId);
        result.ImageStoragePath.Should().StartWith($"/uploads/containers/{result.Id}/");
        result.ImageStoragePath.Should().EndWith(".jpg");
        await _fileStorage.Received(1).SaveAsync(
            Arg.Is<string>(k => k.StartsWith($"uploads/containers/{result.Id}/") && k.EndsWith(".jpg")),
            Arg.Any<Stream>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateAsync_RemoveMainImage_DeletesMainImage()
    {
        var container = await SeedContainerAsync(imagePath: "uploads/containers/id/main-guid.jpg");

        await _sut.UpdateAsync(container.Id, new ContainerData(
            CampId: container.CampId,
            Name: container.Name,
            Description: null,
            RemoveMainImage: true), actorUserId: Guid.NewGuid());

        await _fileStorage.Received(1).DeleteAsync("uploads/containers/id/main-guid.jpg", Arg.Any<CancellationToken>());

        var updated = await _sut.GetByIdAsync(container.Id);
        updated!.ImageStoragePath.Should().BeNull();
    }

    [HumansFact]
    public async Task UpdateAsync_ReplaceMainImage_DeletesPriorMainAndSavesNew()
    {
        var container = await SeedContainerAsync(imagePath: "uploads/containers/id/main-old.jpg");

        await _sut.UpdateAsync(container.Id, new ContainerData(
            CampId: container.CampId,
            Name: container.Name,
            Description: null,
            MainImage: FakeImage("main")), actorUserId: Guid.NewGuid());

        await _fileStorage.Received(1).DeleteAsync("uploads/containers/id/main-old.jpg", Arg.Any<CancellationToken>());

        var updated = await _sut.GetByIdAsync(container.Id);
        updated!.ImageStoragePath.Should().StartWith($"/uploads/containers/{container.Id}/");
        updated.ImageStoragePath.Should().EndWith(".jpg");
    }

    [HumansFact]
    public async Task DeleteAsync_RemovesMainImage()
    {
        var container = await SeedContainerAsync(imagePath: "uploads/containers/id/main.jpg");

        await _sut.DeleteAsync(container.Id, actorUserId: Guid.NewGuid());

        await _fileStorage.Received(1).DeleteAsync("uploads/containers/id/main.jpg", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateAsync_RejectsImageWithUnsupportedExtension()
    {
        var act = async () => await _sut.CreateAsync(actorUserId: Guid.NewGuid(), data: new ContainerData(
            CampId: CampId,
            Name: "Bad",
            Description: null,
            MainImage: new(Stream.Null, "image/jpeg", "trojan.html", 1024)));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*end in .jpg*");
    }
}
