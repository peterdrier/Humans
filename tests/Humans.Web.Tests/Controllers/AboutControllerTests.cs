using Humans.Auth.Contracts;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Humans.Web.Controllers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers;

public class AboutControllerTests
{
    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Index_UnavailableStatisticsKeepThePageUsable(bool failLookup, bool accessDenied)
    {
        Exception failure = accessDenied
            ? new UnauthorizedAccessException("Statistics file cannot be read")
            : new IOException("Statistics file unavailable");
        var file = Substitute.For<IFileInfo>();
        file.Exists.Returns(true);
        file.CreateReadStream().Returns(_ => throw failure);
        var files = Substitute.For<IFileProvider>();
        files.GetFileInfo("data/dev-stats.json").Returns(_ => failLookup ? throw failure : file);
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.WebRootFileProvider.Returns(files);
        var logger = Substitute.For<ILogger<AboutController>>();
        var controller = new AboutController(Substitute.For<IUserService>(), Substitute.For<ISystemRoleService>(),
            new RoleCatalog(Array.Empty<ISectionRoles>()), SystemClock.Instance, logger);

        var view = Assert.IsType<ViewResult>(controller.Index(environment));

        Assert.Null(view.Model);
        var log = Assert.Single(logger.ReceivedCalls(),
            call => string.Equals(call.GetMethodInfo().Name, "Log", StringComparison.Ordinal)).GetArguments();
        Assert.Equal(LogLevel.Error, log[0]);
        Assert.Same(failure, log[3]);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Staff_CallerCancellationStopsRoleAndAssigneeReads(bool atAssigneeRead)
    {
        using var request = new CancellationTokenSource();
        var users = Substitute.For<IUserService>();
        var roles = Substitute.For<ISystemRoleService>();
        roles.GetFilteredAsync(null, true, 1, 500, Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (!atAssigneeRead)
                {
                    await request.CancelAsync();
                    call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                }
                return ((IReadOnlyList<SystemRoleSummarySnapshot>)[], 0);
            });
        async ValueTask<IReadOnlyDictionary<Guid, UserInfo>> ReadUsersAsync(CancellationToken ct)
        {
            await request.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return new Dictionary<Guid, UserInfo>();
        }
        users.GetUserInfosAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => ReadUsersAsync(call.Arg<CancellationToken>()));
        var controller = new AboutController(users, roles, new RoleCatalog(Array.Empty<ISectionRoles>()),
            SystemClock.Instance, NullLogger<AboutController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestAborted = request.Token } }
        };

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.Staff());

        Assert.Equal(request.Token, error.CancellationToken);
    }

    [HumansFact]
    public async Task Staff_UnrelatedDependencyCancellationKeepsEmptyPageFallback()
    {
        var roles = Substitute.For<ISystemRoleService>();
        roles.GetFilteredAsync(null, true, 1, 500, Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(IReadOnlyList<SystemRoleSummarySnapshot>, int)>(new OperationCanceledException()));
        var controller = new AboutController(Substitute.For<IUserService>(), roles,
            new RoleCatalog(Array.Empty<ISectionRoles>()), SystemClock.Instance, NullLogger<AboutController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var view = Assert.IsType<ViewResult>(await controller.Staff());

        Assert.Empty(Assert.IsType<StaffViewModel>(view.Model).RoleSections);
    }

    [HumansFact]
    public void Logos_ListsReadableEntriesInTimelineOrder()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            void Entry(string slug, string? json)
            {
                var dir = Directory.CreateDirectory(Path.Combine(root.FullName, "img", "logo-library", slug));
                if (json is not null)
                {
                    File.WriteAllText(Path.Combine(dir.FullName, "entry.json"), json);
                }
            }
            Entry("2026-11-opus-5.5-high", """{"date":"2026-11","model":"Claude Opus 5.5","effort":"high"}""");
            Entry("2026-11-opus-5.5-low", """{"date":"2026-11","model":"Claude Opus 5.5","effort":"low"}""");
            Entry("2026-11-haiku-5.5-medium", """{"date":"2026-11","model":"Claude Haiku 5.5","effort":"medium"}""");
            Entry("2026-02-opus-4.6-unrecorded", """{"date":"2026-02","model":"Claude Opus 4.6","effort":null}""");
            Entry("2026-11-broken", "{not json");
            Entry("2026-11-unfinished", null);
            using var files = new PhysicalFileProvider(root.FullName);
            var environment = Substitute.For<IWebHostEnvironment>();
            environment.WebRootFileProvider.Returns(files);
            var controller = new AboutController(Substitute.For<IUserService>(), Substitute.For<ISystemRoleService>(),
                new RoleCatalog(Array.Empty<ISectionRoles>()), SystemClock.Instance, NullLogger<AboutController>.Instance);

            var view = Assert.IsType<ViewResult>(controller.Logos(environment));

            Assert.Equal(
                "2026-02-opus-4.6-unrecorded,2026-11-haiku-5.5-medium,2026-11-opus-5.5-low,2026-11-opus-5.5-high",
                string.Join(',', Assert.IsAssignableFrom<IReadOnlyList<LogoLibraryEntry>>(view.Model).Select(e => e.Slug)));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
