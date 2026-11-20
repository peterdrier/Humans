using Humans.Auth.Contracts;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Humans.Web.Controllers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers;

public class AboutControllerTests
{
    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Staff_CallerCancellationStopsRoleAndAssigneeReads(bool atAssigneeRead)
    {
        using var request = new CancellationTokenSource();
        var users = Substitute.For<IUserService>();
        var roles = Substitute.For<IRoleAssignmentService>();
        roles.GetFilteredAsync(null, true, 1, 500, Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (!atAssigneeRead)
                {
                    await request.CancelAsync();
                    call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                }
                return ((IReadOnlyList<RoleAssignmentSummarySnapshot>)[], 0);
            });
        async ValueTask<IReadOnlyDictionary<Guid, UserInfo>> ReadUsersAsync(CancellationToken ct)
        {
            await request.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return new Dictionary<Guid, UserInfo>();
        }
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
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
        var roles = Substitute.For<IRoleAssignmentService>();
        roles.GetFilteredAsync(null, true, 1, 500, Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(IReadOnlyList<RoleAssignmentSummarySnapshot>, int)>(new OperationCanceledException()));
        var controller = new AboutController(Substitute.For<IUserService>(), roles,
            new RoleCatalog(Array.Empty<ISectionRoles>()), SystemClock.Instance, NullLogger<AboutController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var view = Assert.IsType<ViewResult>(await controller.Staff());

        Assert.Empty(Assert.IsType<StaffViewModel>(view.Model).RoleSections);
    }
}
