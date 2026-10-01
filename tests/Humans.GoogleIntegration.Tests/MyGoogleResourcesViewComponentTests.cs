using AwesomeAssertions;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.ViewComponents;
using Humans.Governance.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Security.Claims;

namespace Humans.GoogleIntegration.Tests;

public sealed class MyGoogleResourcesViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var membership = Substitute.For<IMembershipCalculatorRead>();
        membership.GetMembershipSnapshotAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(Task.FromException<MembershipSnapshot>(new OperationCanceledException(aborted.Token)));
        var httpContext = new DefaultHttpContext { RequestAborted = aborted.Token };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));

        var component = new MyGoogleResourcesViewComponent(
            Substitute.For<ITeamResourceService>(), membership,
            NullLogger<MyGoogleResourcesViewComponent>.Instance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = httpContext
                }
            }
        };

        var act = () => component.InvokeAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
