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
using Xunit;

namespace Humans.Containers.Tests.Services;

public sealed class ServiceImageTests
{
    private readonly IFileStorage _fileStorage;
    private readonly Service _sut;
    private static readonly Instant StartTime = Instant.FromUtc(2026, 5, 8, 10, 0, 0);
    private static readonly Guid CampId = Guid.Parse("00000000-0000-0000-0099-000000000001");

    private readonly DbContextOptions<ContainersDbContext> _containersOptions =
        new DbContextOptionsBuilder<ContainersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    public ServiceImageTests()
    {
        _fileStorage = Substitute.For<IFileStorage>();
        var repo = new Repository(new TestDbContextFactory<ContainersDbContext>(_containersOptions));
        _sut = new Service(
            repo,
            _fileStorage,
            Substitute.For<ICampServiceRead>(),
            Substitute.For<IAuditLogService>(),
            new FakeClock(StartTime));
    }

    private static ContainerImageUpload FakeImage(string kind = "main") =>
        new(Stream.Null, "image/jpeg", $"{kind}-sketch.jpg", 1024);

    private static IReadOnlyList<ContainerImageUpload> FakeImages(int count) =>
        Enumerable.Range(0, count).Select(i => FakeImage($"img{i}")).ToList();

    private async Task<Container> SeedContainerAsync(string? legacyImagePath = null, int galleryImages = 0)
    {
        await using var ctx = new ContainersDbContext(_containersOptions);
        var container = new Container
        {
            Id = Guid.NewGuid(),
            CampId = CampId,
            Name = "Container A",
            ImageStoragePath = legacyImagePath,
            ImageContentType = legacyImagePath is not null ? "image/jpeg" : null,
            ImageFileName = legacyImagePath is not null ? "main.jpg" : null,
            CreatedAt = StartTime,
            UpdatedAt = StartTime,
        };
        ctx.Containers.Add(container);
        for (var i = 0; i < galleryImages; i++)
        {
            ctx.ContainerImages.Add(new ContainerImage
            {
                Id = Guid.NewGuid(),
                ContainerId = container.Id,
                StoragePath = $"uploads/containers/{container.Id}/seed-{i}.jpg",
                ContentType = "image/jpeg",
                FileName = $"seed-{i}.jpg",
                SortOrder = i,
                CreatedAt = StartTime,
            });
        }
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        return container;
    }

    [HumansFact]
    public async Task CreateAsync_WithImages_SavesEachUnderContainersPrefix()
    {
        var result = await _sut.CreateAsync(actorUserId: Guid.NewGuid(), data: new ContainerData(
            CampId: CampId,
            Name: "Test",
            Description: null,
            NewImages: FakeImages(3)), ct: TestContext.Current.CancellationToken);

        result.CampId.Should().Be(CampId);
        result.Images.Should().HaveCount(3);
        result.Images.Should().AllSatisfy(i =>
        {
            i.Url.Should().StartWith($"/uploads/containers/{result.Id}/");
            i.Url.Should().EndWith(".jpg");
        });
        await _fileStorage.Received(3).SaveAsync(
            Arg.Is<string>(k => k.StartsWith($"uploads/containers/{result.Id}/") && k.EndsWith(".jpg")),
            Arg.Any<Stream>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateAsync_RejectsMoreThanFiveImages()
    {
        var act = async () => await _sut.CreateAsync(actorUserId: Guid.NewGuid(), data: new ContainerData(
            CampId: CampId,
            Name: "Test",
            Description: null,
            NewImages: FakeImages(6)), ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*at most 5 images*");
    }

    [HumansFact]
    public async Task UpdateAsync_RejectsWhenAddedImagesWouldExceedFive()
    {
        var container = await SeedContainerAsync(galleryImages: 4);

        var act = async () => await _sut.UpdateAsync(container.Id, new ContainerData(
            CampId: container.CampId,
            Name: container.Name,
            Description: null,
            NewImages: FakeImages(2)), actorUserId: Guid.NewGuid(), ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*at most 5 images*");
    }

    [HumansFact]
    public async Task UpdateAsync_CountsTheLegacyImageAgainstTheCap()
    {
        var container = await SeedContainerAsync(legacyImagePath: "uploads/containers/id/main.jpg", galleryImages: 4);

        var act = async () => await _sut.UpdateAsync(container.Id, new ContainerData(
            CampId: container.CampId,
            Name: container.Name,
            Description: null,
            NewImages: FakeImages(1)), actorUserId: Guid.NewGuid(), ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*at most 5 images*");
    }

    [HumansFact]
    public async Task UpdateAsync_RemovingFreesRoomForNewImages()
    {
        var container = await SeedContainerAsync(galleryImages: 5);
        var existing = await _sut.GetByIdAsync(container.Id, TestContext.Current.CancellationToken);

        var updated = await _sut.UpdateAsync(container.Id, new ContainerData(
            CampId: container.CampId,
            Name: container.Name,
            Description: null,
            NewImages: FakeImages(2),
            RemoveImageIds: [existing!.Images[0].Id, existing.Images[1].Id]),
            actorUserId: Guid.NewGuid(), ct: TestContext.Current.CancellationToken);

        updated.Images.Should().HaveCount(5);
        updated.Images.Select(i => i.Id).Should().NotContain(existing.Images[0].Id);
    }

    [HumansFact]
    public async Task UpdateAsync_RemovesOneImageAndItsFile()
    {
        var container = await SeedContainerAsync(galleryImages: 3);
        var existing = await _sut.GetByIdAsync(container.Id, TestContext.Current.CancellationToken);
        var doomed = existing!.Images[1];

        var updated = await _sut.UpdateAsync(container.Id, new ContainerData(
            CampId: container.CampId,
            Name: container.Name,
            Description: null,
            RemoveImageIds: [doomed.Id]),
            actorUserId: Guid.NewGuid(), ct: TestContext.Current.CancellationToken);

        await _fileStorage.Received(1).DeleteAsync(
            doomed.Url.TrimStart('/'), Arg.Any<CancellationToken>());
        updated.Images.Should().HaveCount(2);
        updated.Images.Select(i => i.Id).Should().NotContain(doomed.Id);
    }

    [HumansFact]
    public async Task GetByIdAsync_SurfacesTheLegacyImageFirstAsGuidEmpty()
    {
        var container = await SeedContainerAsync(
            legacyImagePath: "uploads/containers/id/main.jpg", galleryImages: 2);

        var dto = await _sut.GetByIdAsync(container.Id, TestContext.Current.CancellationToken);

        dto!.Images.Should().HaveCount(3);
        dto.Images[0].Id.Should().Be(Guid.Empty);
        dto.Images[0].Url.Should().Be("/uploads/containers/id/main.jpg");
    }

    [HumansFact]
    public async Task UpdateAsync_RemovingGuidEmpty_ClearsTheLegacyImage()
    {
        var container = await SeedContainerAsync(legacyImagePath: "uploads/containers/id/main-guid.jpg");

        var updated = await _sut.UpdateAsync(container.Id, new ContainerData(
            CampId: container.CampId,
            Name: container.Name,
            Description: null,
            RemoveImageIds: [Guid.Empty]), actorUserId: Guid.NewGuid(), ct: TestContext.Current.CancellationToken);

        await _fileStorage.Received(1).DeleteAsync("uploads/containers/id/main-guid.jpg", Arg.Any<CancellationToken>());
        updated.Images.Should().BeEmpty();
    }

    [HumansFact]
    public async Task DeleteAsync_RemovesLegacyAndGalleryImageFiles()
    {
        var container = await SeedContainerAsync(
            legacyImagePath: "uploads/containers/id/main.jpg", galleryImages: 2);

        await _sut.DeleteAsync(container.Id, actorUserId: Guid.NewGuid(), ct: TestContext.Current.CancellationToken);

        await _fileStorage.Received(1).DeleteAsync("uploads/containers/id/main.jpg", Arg.Any<CancellationToken>());
        await _fileStorage.Received(1).DeleteAsync(
            $"uploads/containers/{container.Id}/seed-0.jpg", Arg.Any<CancellationToken>());
        await _fileStorage.Received(1).DeleteAsync(
            $"uploads/containers/{container.Id}/seed-1.jpg", Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData("Dollar $ name")]
    [InlineData("<script>")]
    [InlineData("a > b")]
    public async Task CreateAsync_RejectsNameWithTokenSignificantCharacters(string name)
    {
        var act = async () => await _sut.CreateAsync(actorUserId: Guid.NewGuid(), data: new ContainerData(
            CampId: CampId,
            Name: name,
            Description: null), ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must not contain*");
    }

    [HumansFact]
    public async Task CreateAsync_RejectsImageWithUnsupportedExtension()
    {
        var act = async () => await _sut.CreateAsync(actorUserId: Guid.NewGuid(), data: new ContainerData(
            CampId: CampId,
            Name: "Bad",
            Description: null,
            NewImages: [new(Stream.Null, "image/jpeg", "trojan.html", 1024)]), ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*end in .jpg*");
    }

    [HumansFact]
    public async Task CreateAsync_ValidatesEveryImage_NotJustTheFirst()
    {
        var act = async () => await _sut.CreateAsync(actorUserId: Guid.NewGuid(), data: new ContainerData(
            CampId: CampId,
            Name: "Bad",
            Description: null,
            NewImages: [FakeImage(), new(Stream.Null, "image/gif", "anim.gif", 1024)]),
            ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*JPEG, PNG, and WebP*");
    }
}
