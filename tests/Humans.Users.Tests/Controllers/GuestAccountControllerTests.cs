using Humans.Users.Services;
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
using Microsoft.Extensions.Localization;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Users.Tests.Controllers;

/// <summary>
/// Verifies <see cref="GuestAccountController.RequestDeletion"/>'s flash-message
/// mapping.
/// </summary>
public class GuestAccountControllerTests
{
    private readonly IUserServiceRead _userService = Substitute.For<IUserServiceRead>();
    private readonly ICommunicationPreferenceService _commPrefService = Substitute.For<ICommunicationPreferenceService>();
    private readonly ITicketServiceRead _ticketQueryService = Substitute.For<ITicketServiceRead>();
    private readonly IAccountDeletionService _accountDeletionService = Substitute.For<IAccountDeletionService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IStringLocalizer<UsersResource> _localizer = Substitute.For<IStringLocalizer<UsersResource>>();

    private GuestAccountController BuildSut(User user)
    {
        _localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.ArgAt<string>(0);
            var value = key switch
            {
                "Profile_DeletionAlreadyPending" => "A deletion request is already pending.",
                "Users_Guest_DeletionRequested" => "Deletion request recorded. Your account will be permanently deleted on {0}.",
                _ => key,
            };
            return new LocalizedString(key, value);
        });

        _userService.GetUserInfoAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoFactory.Create(
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
            NullLogger<GuestAccountController>.Instance,
            _localizer);

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

    [HumansTheory]
    [InlineData("Viewer")]
    [InlineData("TokenViewer")]
    [InlineData("Preferences")]
    [InlineData("Tickets")]
    public async Task CommunicationPreferences_StopLoadingAfterRequestCancellation(string boundary)
    {
        using var request = new CancellationTokenSource();
        var user = new User { Id = Guid.NewGuid(), DisplayName = "Human" };
        var ctrl = BuildSut(user);
        ctrl.HttpContext.RequestAborted = request.Token;
        var abandon = false;
        var tokenMode = string.Equals(boundary, "TokenViewer", StringComparison.Ordinal);
        if (tokenMode)
        {
            ctrl.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            _commPrefService.ValidateUnsubscribeToken("valid-token")
                .Returns((TokenValidationStatus.Valid, user.Id, MessageCategory.Marketing));
        }
        async ValueTask<UserInfo?> ReadUser(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (abandon && string.Equals(boundary, "Preferences", StringComparison.Ordinal))
                await request.CancelAsync();
            return UserInfo.Create(user, [], [], [], null, []);
        }
        _userService.GetUserInfoAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(call => ReadUser(call.Arg<CancellationToken>()));
        async Task<IReadOnlyList<CommunicationPreferenceSnapshot>> ReadPreferences(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (abandon && string.Equals(boundary, "Tickets", StringComparison.Ordinal))
                await request.CancelAsync();
            return [];
        }
        _commPrefService.GetPreferencesReadOnlyAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(call => ReadPreferences(call.Arg<CancellationToken>()));
        _ticketQueryService.GetUserTicketHoldingsAsync(user.Id, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new UserTicketHoldings(0, []);
        });
        Func<Task<IActionResult>> load = () => ctrl.CommunicationPreferences(tokenMode ? "valid-token" : null);
        Assert.IsType<ViewResult>(await load());
        abandon = true;
        if (string.Equals(boundary, "Viewer", StringComparison.Ordinal) || tokenMode)
            await request.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(load);
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
