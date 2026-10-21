using AwesomeAssertions;
using Humans.Governance.Contracts;
using Humans.Governance.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;
using System.Security.Claims;

namespace Humans.Governance.Tests.ViewComponents;

public sealed class MemberTermStatusViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var governance = Substitute.For<IGovernanceService>();
        governance.GetMembershipTierAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(_ => Task.FromException<MembershipTier>(new OperationCanceledException(aborted.Token)));
        var httpContext = new DefaultHttpContext { RequestAborted = aborted.Token };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));

        var component = new MemberTermStatusViewComponent(
            Substitute.For<IMembershipCalculator>(), governance, SystemClock.Instance)
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
