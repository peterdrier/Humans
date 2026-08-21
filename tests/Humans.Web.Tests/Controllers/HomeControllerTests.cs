using System.Security.Claims;
using Humans.Base.Configuration;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;
using Humans.Users.Contracts;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Verifies <see cref="HomeController.Index"/> after the name-only access switch. The
/// <see cref="Humans.Web.Authorization.MembershipRequiredFilter"/> now routes every non-Active user
/// away before the action runs, so the controller no longer gates on onboarding completion: it
/// renders the dashboard for any authenticated, resolvable user and the public landing view for
/// anonymous visitors. The dashboard body itself is section-contributed chrome
/// (nobodies-collective/Humans#1091) — this controller only resolves UserInfo facts.
/// </summary>
public class HomeControllerTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly ConfigurationRegistry _configRegistry = new();

    private HomeController BuildSut(ClaimsPrincipal principal)
    {
        var ctrl = new HomeController(
            _userService,
            _configuration,
            _configRegistry);

        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal },
        };
        return ctrl;
    }

    private static ClaimsPrincipal Authenticated(Guid userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));

    private void StubUserInfo(Guid userId)
    {
        var user = new User { Id = userId, DisplayName = "Test" };
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(
                user, [], [], [], profile: null, [])));
    }

    [HumansFact]
    public async Task Index_RendersDashboard_ForAuthenticatedUser()
    {
        var userId = Guid.NewGuid();
        StubUserInfo(userId);

        var result = await BuildSut(Authenticated(userId)).Index();

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Dashboard", view.ViewName);
    }

    [HumansFact]
    public async Task Index_RendersLandingView_ForAnonymousVisitor()
    {
        var result = await BuildSut(new ClaimsPrincipal(new ClaimsIdentity()))
            .Index();

        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.ViewName);
    }
}
