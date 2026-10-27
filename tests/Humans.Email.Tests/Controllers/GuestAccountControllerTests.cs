using System.Security.Claims;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Humans.Email.Contracts;
using Humans.Email.Controllers;
using Humans.Email.Services;
using Microsoft.AspNetCore.DataProtection;
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

namespace Humans.Email.Tests.Controllers;

/// <summary>
/// <see cref="GuestAccountController"/>: the guest and unsubscribe-link preferences page and its
/// update endpoint (session or token, merge resolution, erased accounts, binding, cancellation).
/// </summary>
public class GuestAccountControllerTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly ICommunicationPreferenceManagement _commPrefService = Substitute.For<ICommunicationPreferenceManagement>();
    private readonly ITicketService _ticketQueryService = Substitute.For<ITicketService>();
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IStringLocalizer<EmailResource> _localizer = Substitute.For<IStringLocalizer<EmailResource>>();

    private GuestAccountController BuildSut(User user)
    {
        _localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.ArgAt<string>(0), call.ArgAt<string>(0)));
        _commPrefService.UpdatePreferenceAsync(Arg.Any<Guid>(), Arg.Any<MessageCategory>(), Arg.Any<bool>(),
            Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(true);

        _userService.GetUserInfoAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(user, [], [], [], profile: null, [])));

        var ctrl = new GuestAccountController(
            _userService,
            _commPrefService,
            new UnsubscribeService(_userService, _commPrefService, _dataProtection, NullLogger<UnsubscribeService>.Instance),
            _ticketQueryService,
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
    [InlineData("emailEnabled", true)]
    [InlineData("alertEnabled", true)]
    [InlineData("emailEnabled", false)]
    [InlineData("alertEnabled", false)]
    public async Task UpdatePreference_InvalidBinding_DoesNotChangeChannels(string field, bool viewerExists)
    {
        var user = new User { Id = Guid.NewGuid(), DisplayName = "Human" };
        var controller = BuildSut(user);
        if (!viewerExists)
            _userService.GetUserInfoAsync(user.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>((UserInfo?)null));
        controller.ModelState.AddModelError(field, "Not a boolean.");
        var result = await controller.UpdatePreference(MessageCategory.Marketing,
            !string.Equals(field, "emailEnabled", StringComparison.Ordinal),
            !string.Equals(field, "alertEnabled", StringComparison.Ordinal), null);

        if (viewerExists) Assert.IsType<BadRequestObjectResult>(result);
        else Assert.IsType<UnauthorizedResult>(result);
        Assert.Empty(_commPrefService.ReceivedCalls());
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("stale-token")]
    public async Task UpdatePreference_ExplicitFalseFlags_RemainValid(string? token)
    {
        var user = new User { Id = Guid.NewGuid(), DisplayName = "Human" };
        var controller = BuildSut(user);
        Assert.IsType<OkResult>(await controller.UpdatePreference(MessageCategory.Marketing, false, false, token));
        await _commPrefService.Received(1).UpdatePreferenceAsync(user.Id, MessageCategory.Marketing,
            true, false, "Guest", actorUserId: user.Id, cancellationToken: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdatePreference_AnonymousToken_DoesNotInventAHumanActor()
    {
        var user = new User { Id = Guid.NewGuid(), DisplayName = "Human" };
        var controller = BuildSut(user);
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        _commPrefService.ValidateUnsubscribeToken("valid-token")
            .Returns((TokenValidationStatus.Valid, user.Id, MessageCategory.Marketing));

        Assert.IsType<OkResult>(await controller.UpdatePreference(MessageCategory.Marketing, false, false, "valid-token"));

        await _commPrefService.Received(1).UpdatePreferenceAsync(user.Id, MessageCategory.Marketing,
            true, false, "MagicLink", actorUserId: null, cancellationToken: Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommunicationPreferences_PreMergeToken_UsesTheSurvivingAccount(bool update)
    {
        var archivedId = Guid.NewGuid();
        var survivor = new User { Id = Guid.NewGuid(), DisplayName = "Survivor" };
        var controller = BuildSut(survivor);
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        var resolvedUser = await _userService.GetUserInfoAsync(survivor.Id);
        _userService.GetUserInfoAsync(archivedId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(resolvedUser));
        _commPrefService.ValidateUnsubscribeToken("pre-merge-token")
            .Returns((TokenValidationStatus.Valid, archivedId, MessageCategory.Marketing));
        _commPrefService.GetPreferencesReadOnlyAsync(survivor.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<CommunicationPreferenceSnapshot>());
        _ticketQueryService.GetUserTicketHoldingsAsync(survivor.Id, Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(0, []));

        if (update)
        {
            Assert.IsType<OkResult>(await controller.UpdatePreference(
                MessageCategory.Marketing, false, false, "pre-merge-token"));
            await _commPrefService.Received(1).UpdatePreferenceAsync(survivor.Id,
                MessageCategory.Marketing, true, false, "MagicLink", actorUserId: null,
                cancellationToken: Arg.Any<CancellationToken>());
            await _commPrefService.DidNotReceive().UpdatePreferenceAsync(archivedId,
                Arg.Any<MessageCategory>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        }
        else
        {
            Assert.IsType<ViewResult>(await controller.CommunicationPreferences("pre-merge-token"));
            await _commPrefService.Received(1).GetPreferencesReadOnlyAsync(survivor.Id, Arg.Any<CancellationToken>());
            await _ticketQueryService.Received(1).GetUserTicketHoldingsAsync(survivor.Id, Arg.Any<CancellationToken>());
            await _commPrefService.DidNotReceive().GetPreferencesReadOnlyAsync(archivedId, Arg.Any<CancellationToken>());
        }
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CommunicationPreferences_ErasedAccount_IsRefused(bool signedIn, bool update)
    {
        var userId = Guid.NewGuid();
        var controller = BuildSut(new User
        {
            Id = userId,
            DisplayName = "Deleted User",
            Email = $"deleted-{userId:N}@deleted.local"
        });
        if (!signedIn)
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        _commPrefService.ValidateUnsubscribeToken("pre-erasure-token")
            .Returns((TokenValidationStatus.Valid, userId, MessageCategory.Marketing));
        _commPrefService.GetPreferencesReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<CommunicationPreferenceSnapshot>());
        _ticketQueryService.GetUserTicketHoldingsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(0, []));

        if (update)
            Assert.IsType<UnauthorizedResult>(await controller.UpdatePreference(
                MessageCategory.Marketing, false, false, "pre-erasure-token"));
        else
            Assert.IsType<ChallengeResult>(await controller.CommunicationPreferences("pre-erasure-token"));

        await _commPrefService.DidNotReceive().GetPreferencesReadOnlyAsync(userId, Arg.Any<CancellationToken>());
        await _commPrefService.DidNotReceive().UpdatePreferenceAsync(Arg.Any<Guid>(),
            Arg.Any<MessageCategory>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string>(),
            Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommunicationPreferences_LegacyToken_DoesNotGrantPreferenceAccess(bool update)
    {
        var user = new User { Id = Guid.NewGuid(), BurnerName = "Human" };
        var controller = BuildSut(user);
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        var token = _dataProtection.CreateProtector("CampaignUnsubscribe")
            .ToTimeLimitedDataProtector().Protect(user.Id.ToString(), TimeSpan.FromDays(1));
        _commPrefService.ValidateUnsubscribeToken(token)
            .Returns((TokenValidationStatus.Invalid, Guid.Empty, default(MessageCategory)));

        if (update)
            Assert.IsType<UnauthorizedResult>(await controller.UpdatePreference(
                MessageCategory.Marketing, false, false, token));
        else
            Assert.IsType<ChallengeResult>(await controller.CommunicationPreferences(token));

        await _commPrefService.DidNotReceive().GetPreferencesReadOnlyAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _commPrefService.DidNotReceive().UpdatePreferenceAsync(Arg.Any<Guid>(),
            Arg.Any<MessageCategory>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string>(),
            Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
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
        async ValueTask<UserInfo?> ReadUserAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (abandon && string.Equals(boundary, "Preferences", StringComparison.Ordinal))
                await request.CancelAsync();
            return UserInfo.Create(user, [], [], [], null, []);
        }
        _userService.GetUserInfoAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(call => ReadUserAsync(call.Arg<CancellationToken>()));
        async Task<IReadOnlyList<CommunicationPreferenceSnapshot>> ReadPreferencesAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (abandon && string.Equals(boundary, "Tickets", StringComparison.Ordinal))
                await request.CancelAsync();
            return [];
        }
        _commPrefService.GetPreferencesReadOnlyAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(call => ReadPreferencesAsync(call.Arg<CancellationToken>()));
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
}
