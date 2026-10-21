using AwesomeAssertions;
using Humans.Users.Contracts;
using Humans.Users.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;
using Xunit;

namespace Humans.Users.Tests.ViewComponents;

public sealed class UserBadgeViewComponentTests
{
    // The membership-tier badge moved to Governance with commit 11 of the Users decomposition;
    // its test lives in Humans.Governance.Tests.
    [HumansFact]
    public async Task RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(_ => ValueTask.FromException<UserInfo?>(new OperationCanceledException(aborted.Token)));
        var component = new NobodiesEmailBadgeViewComponent(users);
        component.ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext
            {
                HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token }
            }
        };

        Func<Task> act = async () => await component.InvokeAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
