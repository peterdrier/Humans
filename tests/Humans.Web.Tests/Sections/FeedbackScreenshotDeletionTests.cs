using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Sections;

public sealed class FeedbackScreenshotDeletionTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), "feedback-shots-" + Guid.NewGuid().ToString("N"));

    [HumansFact]
    public async Task Lists_every_screenshot_then_deletes_them_all_and_nothing_else()
    {
        Write("uploads/feedback/a/screenshot.png");
        Write("uploads/feedback/b/screenshot.jpg");
        Write("uploads/issues/c/screenshot.png");
        using var sp = Services();
        var migration = ((ISectionDataMigrations)new Humans.Feedback.Section()).Migrations().Single();

        var before = await migration.Status(sp, default);
        var written = await migration.Apply(sp, Guid.NewGuid(), default);
        var after = await migration.Status(sp, default);

        before.PendingRows.Select(r => r[0]).Should().Equal(
            "uploads/feedback/a/screenshot.png", "uploads/feedback/b/screenshot.jpg");
        written.Should().Be(2);
        after.Done.Should().BeTrue();
        File.Exists(Path.Combine(_contentRoot, "wwwroot", "uploads", "issues", "c", "screenshot.png")).Should().BeTrue();
    }

    private void Write(string key)
    {
        var path = Path.Combine(_contentRoot, "wwwroot", key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1]);
    }

    private ServiceProvider Services()
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(_contentRoot);
        return new ServiceCollection()
            .AddSingleton(environment)
            .AddSingleton<IFileStorage>(new FileSystemFileStorage(environment, NullLogger<FileSystemFileStorage>.Instance))
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot))
            Directory.Delete(_contentRoot, recursive: true);
    }
}
