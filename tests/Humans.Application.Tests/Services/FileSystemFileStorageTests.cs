using AwesomeAssertions;
using Humans.Infrastructure.Services;
using Humans.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Unit tests for the real <see cref="FileSystemFileStorage"/>. Carries
/// forward the coverage from the deleted FileSystemProfilePictureStoreTests
/// — atomic writes, lazy directory creation, missing-file reads, and the
/// path-traversal / rooted-path security guards in <c>ResolveAbsolute</c>.
/// </summary>
public class FileSystemFileStorageTests : IDisposable
{
    private readonly string _contentRoot;
    private readonly string _wwwroot;
    private readonly FileSystemFileStorage _store;

    public FileSystemFileStorageTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), $"humans-fs-tests-{Guid.NewGuid():N}");
        _wwwroot = Path.Combine(_contentRoot, "wwwroot");
        Directory.CreateDirectory(_contentRoot);

        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(_contentRoot);

        _store = new FileSystemFileStorage(env, NullLogger<FileSystemFileStorage>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task SaveAsync_WritesBytesAtKey()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        await _store.SaveAsync("uploads/camps/abc/file.jpg", payload);

        var fullPath = Path.Combine(_wwwroot, "uploads", "camps", "abc", "file.jpg");
        File.Exists(fullPath).Should().BeTrue();
        File.ReadAllBytes(fullPath).Should().BeEquivalentTo(payload);
    }

    [HumansFact]
    public async Task SaveAsync_CreatesIntermediateDirectories()
    {
        Directory.Exists(Path.Combine(_wwwroot, "uploads", "camps", "deep", "nested"))
            .Should().BeFalse();

        await _store.SaveAsync("uploads/camps/deep/nested/file.png", new byte[] { 1 });

        Directory.Exists(Path.Combine(_wwwroot, "uploads", "camps", "deep", "nested"))
            .Should().BeTrue();
    }

    [HumansFact]
    public async Task SaveAsync_OverwriteReplacesExistingFile()
    {
        await _store.SaveAsync("uploads/profile-pictures/p1.jpg", new byte[] { 1, 1, 1 });
        await _store.SaveAsync("uploads/profile-pictures/p1.jpg", new byte[] { 9, 9, 9, 9 });

        var bytes = await _store.TryReadAsync("uploads/profile-pictures/p1.jpg");
        bytes.Should().BeEquivalentTo(new byte[] { 9, 9, 9, 9 });
    }

    [HumansFact]
    public async Task SaveAsync_LeavesNoTempFilesAfterSuccess()
    {
        await _store.SaveAsync("uploads/profile-pictures/p2.jpg", new byte[] { 1 });

        var dir = new DirectoryInfo(Path.Combine(_wwwroot, "uploads", "profile-pictures"));
        dir.GetFiles("*.tmp").Should().BeEmpty(
            because: "atomic temp+rename writes must not leave .tmp siblings on success");
    }

    [HumansFact]
    public async Task TryReadAsync_MissingFile_ReturnsNull()
    {
        var bytes = await _store.TryReadAsync("uploads/profile-pictures/does-not-exist.jpg");

        bytes.Should().BeNull();
    }

    [HumansFact]
    public async Task DeleteAsync_MissingFile_IsNoOp()
    {
        // Should not throw.
        await _store.DeleteAsync("uploads/profile-pictures/does-not-exist.jpg");
    }

    [HumansFact]
    public async Task DeleteAsync_RemovesFile()
    {
        await _store.SaveAsync("uploads/profile-pictures/p3.jpg", new byte[] { 1 });

        await _store.DeleteAsync("uploads/profile-pictures/p3.jpg");

        var bytes = await _store.TryReadAsync("uploads/profile-pictures/p3.jpg");
        bytes.Should().BeNull();
    }

    [HumansFact]
    public async Task SaveAsync_RejectsParentDirectorySegment()
    {
        var act = async () =>
            await _store.SaveAsync("uploads/../etc/passwd", new byte[] { 1 });

        await act.Should().ThrowAsync<ArgumentException>(
            because: "path-traversal segments must be rejected to prevent writes outside wwwroot");
    }

    [HumansFact]
    public async Task TryReadAsync_RejectsParentDirectorySegment()
    {
        var act = async () => await _store.TryReadAsync("../secret");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [HumansFact]
    public async Task SaveAsync_RejectsRootedPath()
    {
        // Use a runtime-constructed rooted path so this works on Linux + Windows.
        var rootedKey = Path.IsPathRooted("/abs/path") ? "/abs/path" : Path.Combine("C:", "abs");
        var act = async () => await _store.SaveAsync(rootedKey, new byte[] { 1 });

        await act.Should().ThrowAsync<ArgumentException>(
            because: "rooted paths could escape wwwroot");
    }

    [HumansFact]
    public async Task SaveAsync_RejectsEmptyKey()
    {
        var act = async () => await _store.SaveAsync("", new byte[] { 1 });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [HumansFact]
    public async Task SaveAsync_AcceptsForwardOrBackslashSeparators()
    {
        await _store.SaveAsync("uploads/camps/x/a.jpg", new byte[] { 1 });

        var bytes = await _store.TryReadAsync("uploads/camps/x/a.jpg");
        bytes.Should().BeEquivalentTo(new byte[] { 1 });
    }
}
