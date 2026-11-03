using Humans.Consent.Contracts;
using Humans.Base.Constants;
using Humans.Notifications.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Camps.Contracts;
using Humans.Governance.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.Notifications.Controllers;
using Humans.Notifications.Services;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Notifications.Tests.Controllers;

public sealed class NotificationsControllerTests
{
    [HumansTheory]
    [Xunit.InlineData("index", "merged")]
    [Xunit.InlineData("index", "missing")]
    [Xunit.InlineData("index", "cancelled")]
    [Xunit.InlineData("popup", "merged")]
    [Xunit.InlineData("popup", "missing")]
    [Xunit.InlineData("popup", "cancelled")]
    [Xunit.InlineData("resolve", "merged")]
    [Xunit.InlineData("resolve", "missing")]
    [Xunit.InlineData("resolve", "cancelled")]
    [Xunit.InlineData("dismiss", "merged")]
    [Xunit.InlineData("dismiss", "missing")]
    [Xunit.InlineData("dismiss", "cancelled")]
    [Xunit.InlineData("read", "merged")]
    [Xunit.InlineData("read", "missing")]
    [Xunit.InlineData("read", "cancelled")]
    [Xunit.InlineData("read-all", "merged")]
    [Xunit.InlineData("read-all", "missing")]
    [Xunit.InlineData("read-all", "cancelled")]
    [Xunit.InlineData("bulk-resolve", "merged")]
    [Xunit.InlineData("bulk-resolve", "missing")]
    [Xunit.InlineData("bulk-resolve", "cancelled")]
    [Xunit.InlineData("bulk-dismiss", "merged")]
    [Xunit.InlineData("bulk-dismiss", "missing")]
    [Xunit.InlineData("bulk-dismiss", "cancelled")]
    [Xunit.InlineData("click", "merged")]
    [Xunit.InlineData("click", "missing")]
    [Xunit.InlineData("click", "cancelled")]
    public async Task InboxActions_ResolveTheAccountBeforeReadingOrMutating(string action, string account)
    {
        using var request = new CancellationTokenSource();
        var cookieId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(string.Equals(account, "missing", StringComparison.Ordinal)
                ? null : UserInfo.Create(new User { Id = actorId }, [], [], null));
        });
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<UserInfo>());
        var inbox = Substitute.For<INotificationInboxService>();
        inbox.GetInboxAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new NotificationInboxResult());
        inbox.GetPopupAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new NotificationPopupResult());
        inbox.ResolveAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new NotificationActionResult(true));
        inbox.DismissAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new NotificationActionResult(true));
        inbox.MarkReadAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new NotificationActionResult(true));
        inbox.ClickThroughAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var consent = Substitute.For<IConsentService>();
        consent.GetConsentReviewsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, ConsentReviewInfo>());
        var teams = Substitute.For<ITeamService>();
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());
        var camps = Substitute.For<ICampService>();
        camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new CampSettingsInfo(2026, []));
        var applications = Substitute.For<IGovernanceService>();
        applications.GetUnvotedApplicationCountAsync(actorId, Arg.Any<CancellationToken>()).Returns(4);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var localization = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var meters = new NotificationMeterProvider(users, consent, Substitute.For<IGoogleSyncService>(), teams,
            Substitute.For<ITicketSync>(), applications, camps, cache, NullLogger<NotificationMeterProvider>.Instance,
            localization.GetRequiredService<IStringLocalizer<NotificationsResource>>());
        var http = new DefaultHttpContext
        {
            RequestAborted = request.Token,
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, cookieId.ToString()), new Claim(ClaimTypes.Role, RoleNames.Board)], "test"))
        };
        http.Request.Headers.XRequestedWith = "XMLHttpRequest";
        var controller = new NotificationsController(inbox, users, meters, NullLogger<NotificationsController>.Instance)
        { ControllerContext = new() { HttpContext = http } };
        var id = Guid.NewGuid();
        Func<Task<IActionResult>> run = () => action switch
        {
            "index" => controller.Index(null),
            "popup" => controller.GetPopup(),
            "resolve" => controller.Resolve(id),
            "dismiss" => controller.Dismiss(id),
            "read" => controller.MarkRead(id),
            "read-all" => controller.MarkAllRead(),
            "bulk-resolve" => controller.BulkResolve([id]),
            "bulk-dismiss" => controller.BulkDismiss([id]),
            _ => controller.ClickThrough(id)
        };
        if (string.Equals(account, "cancelled", StringComparison.Ordinal))
        {
            await request.CancelAsync();
            await run.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            var result = await run();
            if (string.Equals(account, "missing", StringComparison.Ordinal)) result.Should().BeOfType<UnauthorizedResult>();
            else
            {
                var args = inbox.ReceivedCalls().Should().ContainSingle().Which.GetArguments();
                args.OfType<Guid>().Last().Should().Be(actorId);
                args.OfType<CancellationToken>().Should().ContainSingle().Which.Should().Be(
                    action is "index" or "popup" ? request.Token : CancellationToken.None);
                if (action is "index" or "popup")
                {
                    var model = result is ViewResult view ? view.Model : ((PartialViewResult)result).Model;
                    var rows = model is NotificationInboxViewModel list ? list.Meters : ((NotificationPopupViewModel)model!).Meters;
                    rows.Should().ContainSingle().Which.Count.Should().Be(4);
                }
            }
        }
        if (!string.Equals(account, "merged", StringComparison.Ordinal)) inbox.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task Index_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var inbox = Substitute.For<INotificationInboxService>();
        inbox.GetInboxAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), aborted.Token)
            .Returns(Task.FromException<NotificationInboxResult>(new OperationCanceledException(aborted.Token)));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var localization = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var meterProvider = new NotificationMeterProvider(
            Substitute.For<IUserService>(), Substitute.For<IConsentService>(), Substitute.For<IGoogleSyncService>(),
            Substitute.For<ITeamService>(), Substitute.For<ITicketSync>(),
            Substitute.For<IGovernanceService>(), Substitute.For<ICampService>(), cache,
            NullLogger<NotificationMeterProvider>.Instance,
            localization.GetRequiredService<IStringLocalizer<NotificationsResource>>());
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => UserInfo.Create(new User { Id = call.Arg<Guid>() }, [], [], null));
        var controller = new NotificationsController(inbox, users, meterProvider, NullLogger<NotificationsController>.Instance)
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token } }
        };
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())]));

        var act = () => controller.Index(null);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
    [HumansTheory]
    [Xunit.InlineData(true, true)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, false)]
    public async Task BulkActions_RejectMalformedSelectionsBeforeMutating(bool resolve, bool authenticated)
    {
        var userId = Guid.NewGuid();
        var inbox = Substitute.For<INotificationInboxService>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var localization = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var meterProvider = new NotificationMeterProvider(
            Substitute.For<IUserService>(), Substitute.For<IConsentService>(), Substitute.For<IGoogleSyncService>(),
            Substitute.For<ITeamService>(), Substitute.For<ITicketSync>(),
            Substitute.For<IGovernanceService>(), Substitute.For<ICampService>(), cache,
            NullLogger<NotificationMeterProvider>.Instance,
            localization.GetRequiredService<IStringLocalizer<NotificationsResource>>());
        var context = new DefaultHttpContext();
        if (authenticated)
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => UserInfo.Create(new User { Id = call.Arg<Guid>() }, [], [], null));
        var controller = new NotificationsController(inbox, users, meterProvider, NullLogger<NotificationsController>.Instance)
        {
            ControllerContext = new() { HttpContext = context }
        };
        controller.ModelState.AddModelError("selectedIds[1]", "Invalid identifier");
        var selected = new List<Guid> { Guid.NewGuid(), Guid.Empty };

        var result = resolve
            ? await controller.BulkResolve(selected)
            : await controller.BulkDismiss(selected);

        if (authenticated)
            result.Should().BeOfType<BadRequestObjectResult>();
        else
            result.Should().BeOfType<UnauthorizedResult>();
        await inbox.DidNotReceive().BulkResolveAsync(Arg.Any<List<Guid>>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await inbox.DidNotReceive().BulkDismissAsync(Arg.Any<List<Guid>>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

}
