using AwesomeAssertions;
using Humans.Infrastructure.Services.Expenses;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Humans.Application.Tests.Services.Expenses;

public sealed class ExpenseAttachmentFilesystemStorageTests : IAsyncLifetime
{
    private readonly string _tempRoot;
    private ExpenseAttachmentFilesystemStorage _sut = null!;

    public ExpenseAttachmentFilesystemStorageTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    }

    public ValueTask InitializeAsync()
    {
        _sut = BuildSut(_tempRoot);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        return ValueTask.CompletedTask;
    }

    private static ExpenseAttachmentFilesystemStorage BuildSut(string root)
    {
        var opts = Options.Create(new ExpenseAttachmentFilesystemStorageOptions
        {
            Root = root
        });
        return new ExpenseAttachmentFilesystemStorage(opts,
            NullLogger<ExpenseAttachmentFilesystemStorage>.Instance);
    }

    [HumansFact]
    public async Task RoundTrip_StoreThenOpenRead_YieldsSameBytes()
    {
        var original = new byte[] { 1, 2, 3, 4, 5 };
        using var input = new MemoryStream(original);

        var id = await _sut.StoreAsync(input, ".pdf", "application/pdf");

        await using var readStream = await _sut.OpenReadAsync(id, ".pdf");
        using var ms = new MemoryStream();
        await readStream.CopyToAsync(ms);

        ms.ToArray().Should().Equal(original);
    }

    [HumansFact]
    public async Task DeleteAsync_NonExistentId_IsNoOp()
    {
        // Should not throw for an id that was never stored.
        var act = () => _sut.DeleteAsync(Guid.NewGuid(), ".jpg");
        await act.Should().NotThrowAsync();
    }

    [HumansFact]
    public async Task DeleteAsync_ExistingFile_ThenOpenRead_ThrowsFileNotFound()
    {
        var id = await _sut.StoreAsync(new MemoryStream(new byte[] { 0x00 }), ".png", "image/png");

        await _sut.DeleteAsync(id, ".png");

        var act = () => _sut.OpenReadAsync(id, ".png");
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [HumansFact]
    public async Task PathTraversal_MaliciousExtension_ThrowsArgumentException()
    {
        var act = () => _sut.StoreAsync(
            new MemoryStream(new byte[] { 0x00 }),
            "../../etc/passwd",
            "text/plain");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [HumansFact]
    public async Task DisallowedExtension_ThrowsArgumentException()
    {
        var act = () => _sut.StoreAsync(
            new MemoryStream(new byte[] { 0x00 }),
            ".exe",
            "application/octet-stream");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [HumansFact]
    public async Task OpenReadAsync_UnknownId_ThrowsFileNotFoundException()
    {
        var act = () => _sut.OpenReadAsync(Guid.NewGuid(), ".pdf");
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [HumansFact]
    public void Constructor_RootDoesNotExist_CreatesDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "nested", "dir");
        try
        {
            Directory.Exists(root).Should().BeFalse();
            _ = BuildSut(root);
            Directory.Exists(root).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
