using Humans.Web.Models;
using System.Security.Claims;
using Humans.Base.Configuration;
using Humans.Web.Controllers;
using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;
using Humans.Users.Contracts;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// <see cref="Humans.Web.Authorization.MembershipRequiredFilter"/> routes non-Active users away first, so the
/// controller doesn't gate on onboarding: dashboard for any resolvable user, landing view for anonymous.
/// </summary>
public class HomeControllerTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly ConfigurationRegistry _configRegistry = new();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();

    public HomeControllerTests()
    {
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());
    }

    private HomeController BuildSut(ClaimsPrincipal principal)
    {
        var ctrl = new HomeController(
            _userService,
            _configuration,
            _configRegistry,
            _authorization);

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
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = userId }, [], [], null)));
    }

    [HumansFact]
    public async Task Index_CallerCancellationStopsUserRead()
    {
        using var request = new CancellationTokenSource();
        var userId = Guid.NewGuid();
        async ValueTask<UserInfo?> ReadUserAsync(CancellationToken ct)
        {
            await request.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return null;
        }
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(call => ReadUserAsync(call.Arg<CancellationToken>()));
        var controller = BuildSut(Authenticated(userId));
        controller.HttpContext.RequestAborted = request.Token;

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.Index());

        Assert.Equal(request.Token, error.CancellationToken);
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
    private static HomeController WithException(HomeController ctrl)
    {
        ctrl.HttpContext.Response.StatusCode = 500;
        ctrl.HttpContext.Features.Set<IExceptionHandlerFeature>(
            new ExceptionHandlerFeature { Error = new InvalidOperationException("boom"), Path = "/x" });
        return ctrl;
    }

    [HumansFact]
    public async Task Error_Exception_ShowsCodeButHidesDetailFromOrdinaryUser()
    {
        var result = await WithException(BuildSut(Authenticated(Guid.NewGuid()))).Error();

        var model = Assert.IsType<ErrorViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(500, model.StatusCode);
        Assert.Null(model.ExceptionDetail);
    }

    [HumansFact]
    public async Task Error_Exception_ShowsDetailToBoardOrAdmin()
    {
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), PolicyNames.BoardOrAdmin)
            .Returns(AuthorizationResult.Success());

        var result = await WithException(BuildSut(Authenticated(Guid.NewGuid()))).Error();

        var model = Assert.IsType<ErrorViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Contains("boom", model.ExceptionDetail);
    }

    [HumansFact]
    public async Task Error_StatusCodeRoute_CarriesTheCode()
    {
        var result = await BuildSut(new ClaimsPrincipal(new ClaimsIdentity())).Error(403);

        var model = Assert.IsType<ErrorViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(403, model.StatusCode);
        Assert.Null(model.ExceptionDetail);
    }
}
