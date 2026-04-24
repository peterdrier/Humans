using AwesomeAssertions;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Services.Profiles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Unit tests for <see cref="FileSystemProfilePictureStore"/>. Uses a
/// per-test temp directory so tests never touch the real App_Data root.
/// </summary>
public sealed class FileSystemProfilePictureStoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly FileSystemProfilePictureStore _store;

    public FileSystemProfilePictureStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(),
            "humans-profile-pic-tests-" + Guid.NewGuid().ToString("N"));

        var options = Options.Create(new ProfilePictureStorageOptions { Path = _tempRoot });
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath()); // ignored when Path is absolute

        _store = new FileSystemProfilePictureStore(
            options, env, NullLogger<FileSystemProfilePictureStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [HumansFact]
    public async Task TryReadAsync_WhenNoFile_ReturnsNull()
    {
        var result = await _store.TryReadAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task WriteAsync_ThenTryReadAsync_ReturnsSameBytes()
    {
        var id = Guid.NewGuid();
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        await _store.WriteAsync(id, payload, "image/jpeg");
        var result = await _store.TryReadAsync(id);

        result.Should().NotBeNull();
        result!.Value.Data.Should().BeEquivalentTo(payload);
        result.Value.ContentType.Should().Be("image/jpeg");
    }

    [HumansFact]
    public async Task WriteAsync_OverwritesExistingFile()
    {
        var id = Guid.NewGuid();
        await _store.WriteAsync(id, new byte[] { 1 }, "image/jpeg");
        await _store.WriteAsync(id, new byte[] { 2, 3 }, "image/jpeg");

        var result = await _store.TryReadAsync(id);

        result.Should().NotBeNull();
        result!.Value.Data.Should().BeEquivalentTo(new byte[] { 2, 3 });
    }

    [HumansFact]
    public async Task WriteAsync_RenamesExtensionWhenContentTypeChanges()
    {
        var id = Guid.NewGuid();
        await _store.WriteAsync(id, new byte[] { 1 }, "image/jpeg");
        await _store.WriteAsync(id, new byte[] { 2 }, "image/png");

        // Only the PNG should remain on disk — the old JPG must be cleaned up.
        Directory.GetFiles(_tempRoot, $"{id}.*")
            .Should().ContainSingle()
            .Which.Should().EndWith(".png");

        var result = await _store.TryReadAsync(id);
        result.Should().NotBeNull();
        result!.Value.ContentType.Should().Be("image/png");
    }

    [HumansFact]
    public async Task DeleteAsync_RemovesStoredFile()
    {
        var id = Guid.NewGuid();
        await _store.WriteAsync(id, new byte[] { 1, 2 }, "image/jpeg");

        await _store.DeleteAsync(id);

        (await _store.TryReadAsync(id)).Should().BeNull();
        Directory.Exists(_tempRoot).Should().BeTrue();
    }

    [HumansFact]
    public async Task DeleteAsync_WhenNoFile_DoesNotThrow()
    {
        var act = async () => await _store.DeleteAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
    }

    [HumansFact]
    public async Task WriteAsync_UnsupportedContentType_Throws()
    {
        var id = Guid.NewGuid();

        var act = async () => await _store.WriteAsync(id, new byte[] { 1 }, "image/gif");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task WriteAsync_CleansUpTempFilesOnSuccess()
    {
        var id = Guid.NewGuid();
        await _store.WriteAsync(id, new byte[] { 1 }, "image/jpeg");

        // Only the final .jpg file should be left — no stray .tmp siblings.
        Directory.GetFiles(_tempRoot, "*.tmp").Should().BeEmpty();
    }

    [HumansFact]
    public async Task WriteAsync_DoesNotBleedAcrossProfiles()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        await _store.WriteAsync(id1, new byte[] { 1 }, "image/jpeg");
        await _store.WriteAsync(id2, new byte[] { 2 }, "image/jpeg");

        var r1 = await _store.TryReadAsync(id1);
        var r2 = await _store.TryReadAsync(id2);

        r1!.Value.Data.Should().BeEquivalentTo(new byte[] { 1 });
        r2!.Value.Data.Should().BeEquivalentTo(new byte[] { 2 });
    }

    [HumansFact]
    public async Task DeleteAsync_OnlyRemovesTargetProfile()
    {
        var kept = Guid.NewGuid();
        var dropped = Guid.NewGuid();

        await _store.WriteAsync(kept, new byte[] { 1 }, "image/jpeg");
        await _store.WriteAsync(dropped, new byte[] { 2 }, "image/jpeg");

        await _store.DeleteAsync(dropped);

        (await _store.TryReadAsync(kept)).Should().NotBeNull();
        (await _store.TryReadAsync(dropped)).Should().BeNull();
    }

    [HumansFact]
    public async Task ReadThroughFallback_FirstReadMisses_SecondReadHitsAfterMigration()
    {
        // Simulates ProfileController.Picture's read path:
        //   1. TryReadAsync misses (no file yet)
        //   2. Caller fetches from DB and calls WriteAsync (migrate-on-read)
        //   3. Subsequent TryReadAsync hits the filesystem copy.
        var id = Guid.NewGuid();
        var dbPayload = new byte[] { 9, 8, 7, 6 };

        var firstAttempt = await _store.TryReadAsync(id);
        firstAttempt.Should().BeNull("first read must miss — nothing on disk");

        // Simulate migrate-on-read.
        await _store.WriteAsync(id, dbPayload, "image/jpeg");

        var secondAttempt = await _store.TryReadAsync(id);
        secondAttempt.Should().NotBeNull();
        secondAttempt!.Value.Data.Should().BeEquivalentTo(dbPayload);
        secondAttempt.Value.ContentType.Should().Be("image/jpeg");
    }

    [HumansFact]
    public async Task RelativePath_IsResolvedAgainstContentRoot()
    {
        // Fresh store with a relative path should land under ContentRootPath.
        var contentRoot = Path.Combine(Path.GetTempPath(),
            "humans-contentroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            var env = Substitute.For<IHostEnvironment>();
            env.ContentRootPath.Returns(contentRoot);
            var options = Options.Create(new ProfilePictureStorageOptions
            {
                Path = Path.Combine("App_Data", "profile-pictures")
            });

            var store = new FileSystemProfilePictureStore(
                options, env, NullLogger<FileSystemProfilePictureStore>.Instance);

            var id = Guid.NewGuid();
            await store.WriteAsync(id, new byte[] { 9 }, "image/jpeg");

            var expectedPath = Path.Combine(contentRoot, "App_Data", "profile-pictures", $"{id}.jpg");
            File.Exists(expectedPath).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(contentRoot))
            {
                Directory.Delete(contentRoot, recursive: true);
            }
        }
    }
}
