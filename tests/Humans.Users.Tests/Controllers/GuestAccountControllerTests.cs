using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Controllers;
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

namespace Humans.Users.Tests.Controllers;

/// <summary>
/// Verifies <see cref="GuestAccountController.RequestDeletion"/>'s flash-message
/// mapping. Moved from Humans.Web.Tests with the controller (nobodies-collective/Humans#1091).
/// </summary>
public class GuestAccountControllerTests
{
    private readonly IUserServiceRead _userService = Substitute.For<IUserServiceRead>();
    private readonly ICommunicationPreferenceService _commPrefService = Substitute.For<ICommunicationPreferenceService>();
    private readonly ITicketServiceRead _ticketQueryService = Substitute.For<ITicketServiceRead>();
    private readonly IAccountDeletionService _accountDeletionService = Substitute.For<IAccountDeletionService>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private GuestAccountController BuildSut(User user)
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

        var ctrl = new GuestAccountController(
            _userService,
            _commPrefService,
            _ticketQueryService,
            _accountDeletionService,
            _clock,
            NullLogger<GuestAccountController>.Instance);

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
    public async Task RequestDeletion_AlreadyPending_RedirectsWithSpecificError()
    {
        var user = new User { Id = Guid.NewGuid(), DisplayName = "Test" };
        _accountDeletionService.RequestDeletionAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(new DeletionRequestResult(false, "AlreadyPending"));
        var ctrl = BuildSut(user);

        var result = await ctrl.RequestDeletion();

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Guest", redirect.ControllerName);
        Assert.Equal("A deletion request is already pending.", ctrl.TempData[TempDataKeys.ErrorMessage]);
    }

    [HumansFact]
    public async Task RequestDeletion_Success_RedirectsWithDeletionMessage()
    {
        var user = new User { Id = Guid.NewGuid(), DisplayName = "Test" };
        _accountDeletionService.RequestDeletionAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(new DeletionRequestResult(
                true,
                EffectiveDeletionDate: Instant.FromUtc(2026, 6, 15, 0, 0)));
        var ctrl = BuildSut(user);

        var result = await ctrl.RequestDeletion();

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Guest", redirect.ControllerName);
        var message = Assert.IsType<string>(ctrl.TempData[TempDataKeys.SuccessMessage]);
        Assert.Contains("permanently deleted", message, StringComparison.Ordinal);
    }
}
