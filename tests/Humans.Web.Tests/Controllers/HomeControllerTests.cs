using Humans.Web.Models;
using NodaTime;
using Humans.Consent.Contracts;
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
/// <see cref="Humans.Web.Authorization.MembershipRequiredFilter"/> routes non-Active users away first, so the
/// controller doesn't gate on onboarding: dashboard for any resolvable user, landing view for anonymous.
/// </summary>
public class HomeControllerTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IConsentService _consentService = Substitute.For<IConsentService>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly ConfigurationRegistry _configRegistry = new();

    private HomeController BuildSut(ClaimsPrincipal principal)
    {
        var ctrl = new HomeController(
            _userService,
            _consentService,
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

    private void StubUserInfo(Guid userId, ProfileInfo? profile = null, ConsentReviewInfo? review = null)
    {
        var user = new User { Id = userId, DisplayName = "Test" };
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(
                user, [], [], [], profile, [])));
        _consentService.GetConsentReviewAsync(userId, Arg.Any<CancellationToken>())
            .Returns(review ?? ConsentReviewInfo.None(userId));
    }

    [HumansFact]
    public async Task Index_Dashboard_ShowsConsentsRecordedRejectionAndReason()
    {
        var userId = Guid.NewGuid();
        StubUserInfo(userId, UserFixtures.Profile(burnerName: "B", firstName: "F", lastName: "L"),
            ConsentReviewInfo.None(userId) with
            {
                RejectionReason = "Not a fit",
                RejectedAt = Instant.FromUtc(2026, 5, 1, 0, 0),
            });

        var result = await BuildSut(Authenticated(userId)).Index();

        var model = Assert.IsType<DashboardViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.True(model.IsRejected);
        Assert.Equal("Not a fit", model.RejectionReason);
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
