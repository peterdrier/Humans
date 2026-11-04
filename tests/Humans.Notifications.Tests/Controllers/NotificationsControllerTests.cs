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
    public enum InboxAction { Index, Popup, Resolve, Dismiss, MarkRead, MarkAllRead, BulkResolve, BulkDismiss, ClickThrough }

    [HumansTheory]
    [Xunit.InlineData(InboxAction.Index, AccountScenario.Merged)]
    [Xunit.InlineData(InboxAction.Index, AccountScenario.Missing)]
    [Xunit.InlineData(InboxAction.Index, AccountScenario.Cancelled)]
    [Xunit.InlineData(InboxAction.Popup, AccountScenario.Merged)]
    [Xunit.InlineData(InboxAction.Popup, AccountScenario.Missing)]
    [Xunit.InlineData(InboxAction.Popup, AccountScenario.Cancelled)]
    [Xunit.InlineData(InboxAction.Resolve, AccountScenario.Merged)]
    [Xunit.InlineData(InboxAction.Resolve, AccountScenario.Missing)]
    [Xunit.InlineData(InboxAction.Resolve, AccountScenario.Cancelled)]
    [Xunit.InlineData(InboxAction.Dismiss, AccountScenario.Merged)]
    [Xunit.InlineData(InboxAction.Dismiss, AccountScenario.Missing)]
    [Xunit.InlineData(InboxAction.Dismiss, AccountScenario.Cancelled)]
    [Xunit.InlineData(InboxAction.MarkRead, AccountScenario.Merged)]
    [Xunit.InlineData(InboxAction.MarkRead, AccountScenario.Missing)]
    [Xunit.InlineData(InboxAction.MarkRead, AccountScenario.Cancelled)]
    [Xunit.InlineData(InboxAction.MarkAllRead, AccountScenario.Merged)]
    [Xunit.InlineData(InboxAction.MarkAllRead, AccountScenario.Missing)]
    [Xunit.InlineData(InboxAction.MarkAllRead, AccountScenario.Cancelled)]
    [Xunit.InlineData(InboxAction.BulkResolve, AccountScenario.Merged)]
    [Xunit.InlineData(InboxAction.BulkResolve, AccountScenario.Missing)]
    [Xunit.InlineData(InboxAction.BulkResolve, AccountScenario.Cancelled)]
    [Xunit.InlineData(InboxAction.BulkDismiss, AccountScenario.Merged)]
    [Xunit.InlineData(InboxAction.BulkDismiss, AccountScenario.Missing)]
    [Xunit.InlineData(InboxAction.BulkDismiss, AccountScenario.Cancelled)]
    [Xunit.InlineData(InboxAction.ClickThrough, AccountScenario.Merged)]
    [Xunit.InlineData(InboxAction.ClickThrough, AccountScenario.Missing)]
    [Xunit.InlineData(InboxAction.ClickThrough, AccountScenario.Cancelled)]
    public async Task InboxActions_ResolveTheAccountBeforeReadingOrMutating(InboxAction action, AccountScenario account)
    {
        using var request = new CancellationTokenSource();
        var cookieId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(account == AccountScenario.Missing
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
            InboxAction.Index => controller.Index(null),
            InboxAction.Popup => controller.GetPopup(),
            InboxAction.Resolve => controller.Resolve(id),
            InboxAction.Dismiss => controller.Dismiss(id),
            InboxAction.MarkRead => controller.MarkRead(id),
            InboxAction.MarkAllRead => controller.MarkAllRead(),
            InboxAction.BulkResolve => controller.BulkResolve([id]),
            InboxAction.BulkDismiss => controller.BulkDismiss([id]),
            InboxAction.ClickThrough => controller.ClickThrough(id),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
        if (account == AccountScenario.Cancelled)
        {
            await request.CancelAsync();
            await run.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            var result = await run();
            if (account == AccountScenario.Missing) result.Should().BeOfType<UnauthorizedResult>();
            else
            {
                var args = inbox.ReceivedCalls().Should().ContainSingle().Which.GetArguments();
                args.OfType<Guid>().Last().Should().Be(actorId);
                args.OfType<CancellationToken>().Should().ContainSingle().Which.Should().Be(
                    action is InboxAction.Index or InboxAction.Popup ? request.Token : CancellationToken.None);
                if (action is InboxAction.Index or InboxAction.Popup)
                {
                    var model = result is ViewResult view ? view.Model : ((PartialViewResult)result).Model;
                    var rows = model is NotificationInboxViewModel list ? list.Meters : ((NotificationPopupViewModel)model!).Meters;
                    rows.Should().ContainSingle().Which.Count.Should().Be(4);
                }
            }
        }
        if (account != AccountScenario.Merged) inbox.ReceivedCalls().Should().BeEmpty();
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
