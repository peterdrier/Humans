using System.Security.Claims;
using Humans.Onboarding.Contracts;
using Humans.Onboarding.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Onboarding.Tests.Controllers;

/// <summary>
/// Verifies that <see cref="GuestController.Index"/> defers to
/// <see cref="IOnboardingWidgetState"/>: incomplete users are redirected into
/// the onboarding widget, complete users fall through to the guest dashboard.
/// Moved from Humans.Web.Tests with the controller (nobodies-collective/Humans#1091).
/// </summary>
public class GuestControllerTests
{
    private readonly IUserServiceRead _userService = Substitute.For<IUserServiceRead>();
    private readonly IOnboardingWidgetState _widgetState = Substitute.For<IOnboardingWidgetState>();

    private GuestController BuildSut(User user)
    {
        _userService.GetUserInfoAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(
                user,
                [],
                [],
                [],
                profile: null,
                [])));

        var ctrl = new GuestController(
            _userService,
            _widgetState,
            NullLogger<GuestController>.Instance);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())],
                "test")),
            RequestServices = new ServiceCollection()
                .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
                .BuildServiceProvider(),
        };
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = "Test" },
        };
        ctrl.Url = Substitute.For<IUrlHelper>();
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return ctrl;
    }

    [HumansFact]
    public async Task Index_RedirectsToOnboardingWidget_WhenStepNotComplete()
    {
        var user = new User { Id = Guid.NewGuid(), DisplayName = "Test" };
        _widgetState.GetCurrentStepAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(OnboardingWidgetStep.Names);
        var ctrl = BuildSut(user);

        var result = await ctrl.Index(TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("OnboardingWidget", redirect.ControllerName);
    }

    [HumansFact]
    public async Task Index_RendersGuestDashboard_WhenStepComplete()
    {
        var user = new User { Id = Guid.NewGuid(), DisplayName = "Test" };
        _widgetState.GetCurrentStepAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(OnboardingWidgetStep.Complete);
        var ctrl = BuildSut(user);

        var result = await ctrl.Index(TestContext.Current.CancellationToken);

        Assert.IsType<ViewResult>(result);
    }
}
