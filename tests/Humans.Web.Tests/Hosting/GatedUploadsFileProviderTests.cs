using AwesomeAssertions;
using Humans.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Hosting;

/// <summary>
/// Drives the real <see cref="StaticFileMiddleware"/> over a temp web root to prove the gated upload
/// folders are unreachable under every spelling of the path, while ordinary static files still serve.
/// </summary>
public sealed class GatedUploadsFileProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gated-uploads-" + Guid.NewGuid().ToString("N"));

    public GatedUploadsFileProviderTests()
    {
        foreach (var file in new[] { "css/site.css", "uploads/issues/x/y.png", "uploads/profile-pictures/p.jpg", "uploads/expense-attachments/a.pdf", "uploads/expense-attachments/a.jpg", "uploads/camps/c.jpg" })
        {
            var full = Path.Combine(_root, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<int> GetStatusAsync(string path)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.WebRootPath.Returns(_root);
        var provider = new GatedUploadsFileProvider(new PhysicalFileProvider(_root));
        env.WebRootFileProvider.Returns(provider);
        var options = new StaticFileOptions { FileProvider = provider, ServeUnknownFileTypes = true };
        var middleware = new StaticFileMiddleware(
            ctx => { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return Task.CompletedTask; },
            env,
            Options.Create(options),
            NullLoggerFactory.Instance);

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        await middleware.Invoke(context);
        return context.Response.StatusCode;
    }

    [HumansTheory]
    [InlineData("/uploads/issues/x/y.png")]
    [InlineData("/uploads//issues/x/y.png")]
    [InlineData("/uploads/./issues/x/y.png")]
    [InlineData("/uploads/issues//x/y.png")]
    [InlineData("/uploads/other/../issues/x/y.png")]
    [InlineData("/UPLOADS/Issues/x/y.png")]
    [InlineData("/uploads\\issues\\x\\y.png")]
    [InlineData("/uploads/profile-pictures/p.jpg")]
    [InlineData("/uploads//profile-pictures/p.jpg")]
    [InlineData("/uploads/expense-attachments/a.pdf")]
    [InlineData("/uploads/expense-attachments/a.jpg")]
    [InlineData("/uploads//expense-attachments/a.pdf")]
    [InlineData("/uploads/other/../expense-attachments/a.pdf")]
    [InlineData("/UPLOADS/EXPENSE-ATTACHMENTS/a.pdf")]
    [InlineData("/uploads\\expense-attachments\\a.pdf")]
    public async Task GatedUploadPath_AnySpelling_IsNotServed(string path)
    {
        (await GetStatusAsync(path)).Should().Be(StatusCodes.Status404NotFound);
    }

    [HumansTheory]
    [InlineData("/css/site.css")]
    [InlineData("/uploads/camps/c.jpg")]
    [InlineData("//css/site.css")]
    public async Task OrdinaryStaticFile_StillServes(string path)
    {
        (await GetStatusAsync(path)).Should().Be(StatusCodes.Status200OK);
    }
}
