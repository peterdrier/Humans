using System.Security.Claims;
using Humans.Application;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Web.Constants;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly ICommunicationPreferenceService _commPrefService = Substitute.For<ICommunicationPreferenceService>();
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly ITicketService _ticketQueryService = Substitute.For<ITicketService>();
    private readonly IGdprExportService _gdprExportService = Substitute.For<IGdprExportService>();
    private readonly IOnboardingWidgetState _widgetState = Substitute.For<IOnboardingWidgetState>();
    private readonly IAccountDeletionService _accountDeletionService = Substitute.For<IAccountDeletionService>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private GuestController BuildSut(User user)
    {
        _userService.GetUserInfoAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(
                user,
                [],
                [],
                [],
                profile: null,
                [],
                [],
                [],
                [])));

        var ctrl = new GuestController(
            _userService,
            _commPrefService,
            _profileService,
            _ticketQueryService,
            _gdprExportService,
            _widgetState,
            _accountDeletionService,
            _clock,
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
        _ticketQueryService.GetUserTicketHoldingsAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(0, []));
        var ctrl = BuildSut(user);

        var result = await ctrl.Index(CancellationToken.None);

        Assert.IsType<ViewResult>(result);
    }

    [HumansFact]
    public async Task RequestDeletion_AlreadyPending_RedirectsWithSpecificError()
    {
        var user = new User { Id = Guid.NewGuid(), DisplayName = "Test" };
        _accountDeletionService.RequestDeletionAsync(user.Id)
            .Returns(new DeletionRequestResult(false, "AlreadyPending"));
        var ctrl = BuildSut(user);

        var result = await ctrl.RequestDeletion();

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("A deletion request is already pending.", ctrl.TempData[TempDataKeys.ErrorMessage]);
    }

    [HumansFact]
    public async Task RequestDeletion_Success_RedirectsWithDeletionMessage()
    {
        var user = new User { Id = Guid.NewGuid(), DisplayName = "Test" };
        _accountDeletionService.RequestDeletionAsync(user.Id)
            .Returns(new DeletionRequestResult(
                true,
                EffectiveDeletionDate: Instant.FromUtc(2026, 6, 15, 0, 0)));
        var ctrl = BuildSut(user);

        var result = await ctrl.RequestDeletion();

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        var message = Assert.IsType<string>(ctrl.TempData[TempDataKeys.SuccessMessage]);
        Assert.Contains("permanently deleted", message, StringComparison.Ordinal);
    }
}
