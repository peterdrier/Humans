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
    public async Task Bell_ResolvesTheAccountBeforeCountingUnreadRows(AccountScenario account)
    {
        var cookieId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            return new ValueTask<UserInfo?>(account == AccountScenario.Missing
                ? null : UserInfo.Create(new User { Id = survivorId }, [], [], null));
        });
        var inbox = Substitute.For<INotificationInboxService>();
        inbox.GetUnreadBadgeCountsAsync(survivorId, Arg.Any<CancellationToken>()).Returns((2, 3));
        using var services = new ServiceCollection().AddSingleton(inbox).AddSingleton(users).BuildServiceProvider();
        var component = ActivatorUtilities.CreateInstance<NotificationBellViewComponent>(services);
        component.ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, cookieId.ToString())], "test"))
                }
            }
        };
        var result = (await component.InvokeAsync()).Should().BeOfType<ViewViewComponentResult>().Subject;
        var badge = result.ViewData!.Model.Should().BeOfType<NotificationBadgeViewModel>().Subject;
        badge.ActionableUnreadCount.Should().Be(account == AccountScenario.Merged ? 2 : 0);
        badge.InformationalUnreadCount.Should().Be(account == AccountScenario.Merged ? 3 : 0);
        if (account != AccountScenario.Merged) inbox.ReceivedCalls().Should().BeEmpty();
    }

}
