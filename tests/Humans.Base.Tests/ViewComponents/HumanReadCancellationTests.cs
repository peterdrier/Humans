using AwesomeAssertions;
using Humans.Base.ViewComponents;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;
using Microsoft.Extensions.Localization;

namespace Humans.Base.Tests.ViewComponents;

public class HumanReadCancellationTests
{
    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task AbandonedRequest_CancelsHumanLookup(bool picker)
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var userId = Guid.NewGuid();
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            var token = call.Arg<CancellationToken>();
            return token.IsCancellationRequested
                ? ValueTask.FromCanceled<UserInfo?>(token)
                : ValueTask.FromResult<UserInfo?>(null);
        });
        ViewComponent component = picker
            ? new HumanSearchViewComponent(users)
            : new HumanViewComponent(users, Substitute.For<IUrlHelperFactory>(), Substitute.For<IStringLocalizer<SharedResource>>());
        component.ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext
            {
                HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token },
            },
        };
        Func<Task> act = () => picker
            ? ((HumanSearchViewComponent)component).InvokeAsync(selectedUserId: userId)
            : ((HumanViewComponent)component).InvokeAsync(userId);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
