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
    private readonly Microsoft.Extensions.Logging.ILogger<Service> _logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<Service>>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();
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
            _auditLog,
            new FakeClock(StartTime), _logger);
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
            .WithMessage("Containers_Error_TooManyImages");
    }

    [HumansFact]
    public async Task CreateAsync_RejectsAnOverlongImageFilenameBeforeWriting()
    {
        var image = new ContainerImageUpload(
            Stream.Null, "image/jpeg", new string('a', 253) + ".jpg", 1024);

        var act = async () => await _sut.CreateAsync(actorUserId: Guid.NewGuid(), data: new ContainerData(
            CampId: CampId,
            Name: "Test",
            Description: null,
            NewImages: [image]), ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Containers_Error_ImageFileNameLength");
        await _fileStorage.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateAsync_StoresOnlyTheImageBasename()
    {
        var image = new ContainerImageUpload(Stream.Null, "image/jpeg", @"C:\fakepath\gallery.jpg", 1024);

        var result = await _sut.CreateAsync(actorUserId: Guid.NewGuid(), data: new ContainerData(
            CampId: CampId,
            Name: "Test",
            Description: null,
            NewImages: [image]), ct: TestContext.Current.CancellationToken);

        result.Images.Should().ContainSingle().Which.FileName.Should().Be("gallery.jpg");
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
            .WithMessage("Containers_Error_TooManyImages");
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
            .WithMessage("Containers_Error_TooManyImages");
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
    public async Task DeleteAsync_preserves_image_files_when_the_database_delete_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var container = await SeedContainerAsync(legacyImagePath: "uploads/containers/legacy.jpg");
        var repo = Substitute.For<IContainerRepository>();
        repo.GetByIdAsync(container.Id, Arg.Any<CancellationToken>()).Returns(container);
        repo.GetImagesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ContainerImage>
            {
                new() { Id = Guid.NewGuid(), ContainerId = container.Id, StoragePath = "uploads/containers/gallery.jpg", ContentType = "image/jpeg", FileName = "gallery.jpg" }
            });
        var original = new IOException("Database delete failed");
        repo.DeleteAsync(container.Id, Arg.Any<CancellationToken>()).Returns(Task.FromException(original));
        var service = new Service(repo, _fileStorage, Substitute.For<ICampServiceRead>(), _auditLog, new FakeClock(StartTime), _logger);

        var act = () => service.DeleteAsync(container.Id, Guid.NewGuid(), ct);

        var thrown = await act.Should().ThrowAsync<IOException>();
        thrown.Which.Should().BeSameAs(original);
        await _fileStorage.DidNotReceiveWithAnyArgs().DeleteAsync(default!);
    }

    [HumansFact]
    public async Task DeleteAsync_commits_and_audits_before_attempting_every_image_cleanup()
    {
        var ct = TestContext.Current.CancellationToken;
        var container = await SeedContainerAsync(legacyImagePath: "uploads/containers/legacy.jpg", galleryImages: 2);
        var actorId = Guid.NewGuid();
        var metadataDeletedBeforeCleanup = false;
        var auditedBeforeCleanup = false;
        var cleanupFailure = new IOException("Image file cannot be deleted");
        _fileStorage.DeleteAsync($"uploads/containers/{container.Id}/seed-0.jpg", Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await using var db = new ContainersDbContext(_containersOptions);
                metadataDeletedBeforeCleanup = await db.Containers.CountAsync(ct) == 0;
                auditedBeforeCleanup = _auditLog.ReceivedCalls().Any(call =>
                    call.GetArguments()[0] is AuditAction action && action == AuditAction.ContainerDeleted);
                await Task.FromException(cleanupFailure);
            });

        var act = () => _sut.DeleteAsync(container.Id, actorId, ct);

        await act.Should().NotThrowAsync();
        await using var ctx = new ContainersDbContext(_containersOptions);
        (await ctx.Containers.CountAsync(ct)).Should().Be(0);
        (await ctx.ContainerImages.CountAsync(ct)).Should().Be(0);
        metadataDeletedBeforeCleanup.Should().BeTrue();
        auditedBeforeCleanup.Should().BeTrue();
        await _auditLog.Received(1).LogAsync(AuditAction.ContainerDeleted, AuditEntityTypes.Container, container.Id,
            Arg.Any<string>(), actorId, CampId, AuditEntityTypes.Camp);
        _logger.ReceivedCalls().Where(call =>
            call.GetArguments()[0] is Microsoft.Extensions.Logging.LogLevel level && level == Microsoft.Extensions.Logging.LogLevel.Error
            && ReferenceEquals(call.GetArguments()[3], cleanupFailure)).Should().ContainSingle();
        await _fileStorage.Received(1).DeleteAsync("uploads/containers/legacy.jpg", Arg.Any<CancellationToken>());
        for (var i = 0; i < 2; i++)
            await _fileStorage.Received(1).DeleteAsync($"uploads/containers/{container.Id}/seed-{i}.jpg", Arg.Any<CancellationToken>());
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
            .WithMessage("Containers_Error_InvalidName");
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
            .WithMessage("Containers_Error_ImageExtension");
    }

    [HumansFact]
    public async Task CreateAsync_RejectsImageOver10MB()
    {
        var act = async () => await _sut.CreateAsync(actorUserId: Guid.NewGuid(), data: new ContainerData(
            CampId: CampId,
            Name: "Big",
            Description: null,
            NewImages: [new(Stream.Null, "image/jpeg", "big.jpg", 10 * 1024 * 1024 + 1)]), ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Containers_Error_ImageSize");
        await _fileStorage.DidNotReceive().SaveAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
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
            .WithMessage("Containers_Error_ImageType");
    }
}
