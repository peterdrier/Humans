using System.Security.Claims;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Entities;
using Humans.Testing;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Verifies that <see cref="GuestController.Index"/> defers to
/// <see cref="IOnboardingWidgetState"/>: incomplete users are redirected into
/// the onboarding widget, complete users fall through to the guest dashboard.
/// </summary>
public class GuestControllerTests
{
    private readonly UserManager<User> _userManager;
    private readonly ICommunicationPreferenceService _commPrefService = Substitute.For<ICommunicationPreferenceService>();
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly ITicketQueryService _ticketQueryService = Substitute.For<ITicketQueryService>();
    private readonly IGdprExportService _gdprExportService = Substitute.For<IGdprExportService>();
    private readonly IOnboardingWidgetState _widgetState = Substitute.For<IOnboardingWidgetState>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public GuestControllerTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
    }

    private GuestController BuildSut(User user)
    {
        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(user);

        var ctrl = new GuestController(
            _userManager,
            _commPrefService,
            _profileService,
            _ticketQueryService,
            _gdprExportService,
            _widgetState,
            _clock,
            NullLogger<GuestController>.Instance);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()) },
                "test")),
        };
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
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

        var result = await ctrl.Index(CancellationToken.None);

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
        _ticketQueryService.HasTicketAttendeeMatchAsync(user.Id).Returns(false);
        var ctrl = BuildSut(user);

        var result = await ctrl.Index(CancellationToken.None);

        Assert.IsType<ViewResult>(result);
    }
}
