using AwesomeAssertions;
using Humans.Notifications.Services;
using Humans.Notifications.Models;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Humans.Notifications.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;
using System.Security.Claims;

namespace Humans.Notifications.Tests.ViewComponents;

public sealed class NotificationBellViewComponentTests
{
    [HumansTheory]
    [Xunit.InlineData(AccountScenario.Merged)]
    [Xunit.InlineData(AccountScenario.Missing)]
    [Xunit.InlineData(AccountScenario.Cancelled)]
    public async Task Bell_ResolvesTheAccountBeforeCountingUnreadRows(AccountScenario account)
    {
        using var request = new CancellationTokenSource();
        var cookieId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(account == AccountScenario.Missing
                ? null : UserInfo.Create(new User { Id = survivorId }, [], [], null));
        });
        var inbox = Substitute.For<INotificationInboxService>();
        inbox.GetUnreadBadgeCountsAsync(survivorId, request.Token).Returns((2, 3));
        using var services = new ServiceCollection().AddSingleton(inbox).AddSingleton(users).BuildServiceProvider();
        var component = ActivatorUtilities.CreateInstance<NotificationBellViewComponent>(services);
        component.ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestAborted = request.Token,
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, cookieId.ToString())], "test"))
                }
            }
        };
        if (account == AccountScenario.Cancelled)
        {
            await request.CancelAsync();
            Func<Task<IViewComponentResult>> run = component.InvokeAsync;
            await run.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            var result = (await component.InvokeAsync()).Should().BeOfType<ViewViewComponentResult>().Subject;
            var badge = result.ViewData!.Model.Should().BeOfType<NotificationBadgeViewModel>().Subject;
            badge.ActionableUnreadCount.Should().Be(account == AccountScenario.Merged ? 2 : 0);
            badge.InformationalUnreadCount.Should().Be(account == AccountScenario.Merged ? 3 : 0);
        }
        if (account != AccountScenario.Merged) inbox.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var inbox = Substitute.For<INotificationInboxService>();
        inbox.GetUnreadBadgeCountsAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(Task.FromException<(int Actionable, int Informational)>(
                new OperationCanceledException(aborted.Token)));
        var httpContext = new DefaultHttpContext { RequestAborted = aborted.Token };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => UserInfo.Create(new User { Id = call.Arg<Guid>() }, [], [], null));
        var component = new NotificationBellViewComponent(inbox, users)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = httpContext }
            }
        };

        var act = () => component.InvokeAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
