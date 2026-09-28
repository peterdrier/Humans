using AwesomeAssertions;
using Humans.Workgroups.Services;
using Humans.Workgroups.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;
using System.Security.Claims;

namespace Humans.Workgroups.Tests.ViewComponents;

public sealed class MyWorkgroupsViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var workgroups = Substitute.For<IWorkgroupService>();
        workgroups.GetForMemberAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(Task.FromException<IReadOnlyList<WorkgroupInfo>>(
                new OperationCanceledException(aborted.Token)));
        var httpContext = new DefaultHttpContext { RequestAborted = aborted.Token };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));
        var component = new MyWorkgroupsViewComponent(workgroups, SystemClock.Instance)
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
