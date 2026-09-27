using AwesomeAssertions;
using Humans.Users.Contracts;
using Humans.Users.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;
using Xunit;

namespace Humans.Users.Tests.ViewComponents;

public sealed class UserBadgeViewComponentTests
{
    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RequestAborted_PropagatesCancellation(bool membershipTierBadge)
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(_ => ValueTask.FromException<UserInfo?>(new OperationCanceledException(aborted.Token)));
        var component = membershipTierBadge
            ? (ViewComponent)new MembershipTierBadgeViewComponent(users)
            : new NobodiesEmailBadgeViewComponent(users);
        component.ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext
            {
                HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token }
            }
        };

        Func<Task> act = membershipTierBadge
            ? async () => await ((MembershipTierBadgeViewComponent)component).InvokeAsync(Guid.NewGuid())
            : async () => await ((NobodiesEmailBadgeViewComponent)component).InvokeAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
